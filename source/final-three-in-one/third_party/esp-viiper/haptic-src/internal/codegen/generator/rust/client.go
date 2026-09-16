package rust

import (
	"fmt"
	"log/slog"
	"os"
	"path/filepath"
	"text/template"

	"github.com/Alia5/VIIPER/internal/codegen/common"
	"github.com/Alia5/VIIPER/internal/codegen/meta"
	"github.com/Alia5/VIIPER/internal/codegen/scanner"
)

const clientTemplate = `{{.Header}}
use crate::error::{ProblemJson, ViiperError};
use crate::types::*;
use std::io::{Read, Write};
use std::net::{SocketAddr, TcpStream, Shutdown};

/// Stream wrapper that can be either plain or encrypted
enum StreamWrapper {
    Plain(TcpStream),
    Encrypted(crate::auth::EncryptedStream),
}

impl StreamWrapper {
    fn try_clone(&self) -> std::io::Result<Self> {
        match self {
            StreamWrapper::Plain(s) => Ok(StreamWrapper::Plain(s.try_clone()?)),
            StreamWrapper::Encrypted(s) => Ok(StreamWrapper::Encrypted(s.try_clone()?)),
        }
    }
    
    fn shutdown(&self, how: Shutdown) -> std::io::Result<()> {
        match self {
            StreamWrapper::Plain(s) => s.shutdown(how),
            StreamWrapper::Encrypted(s) => s.shutdown(how),
        }
    }
}

impl Read for StreamWrapper {
    fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
        match self {
            StreamWrapper::Plain(s) => s.read(buf),
            StreamWrapper::Encrypted(s) => s.read(buf),
        }
    }
}

impl Write for StreamWrapper {
    fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
        match self {
            StreamWrapper::Plain(s) => s.write(buf),
            StreamWrapper::Encrypted(s) => s.write(buf),
        }
    }
    
    fn flush(&mut self) -> std::io::Result<()> {
        match self {
            StreamWrapper::Plain(s) => s.flush(),
            StreamWrapper::Encrypted(s) => s.flush(),
        }
    }
}

/// VIIPER management API client (synchronous).
pub struct ViiperClient {
    addr: SocketAddr,
    password: Option<String>,
}

impl ViiperClient {
    /// Create a new VIIPER client connecting to the specified address.
    pub fn new(addr: SocketAddr) -> Self {
        Self { addr, password: None }
    }

    /// Create a new VIIPER client with password authentication.
    /// Empty password string explicitly means no authentication.
    pub fn new_with_password(addr: SocketAddr, password: String) -> Self {
        let password = if password.is_empty() { None } else { Some(password) };
        Self { addr, password }
    }

    fn do_request<T: for<'de> serde::Deserialize<'de>>(
        &self,
        path: &str,
        payload: Option<&str>,
    ) -> Result<T, ViiperError> {
        let tcp_stream = TcpStream::connect(self.addr)?;
        tcp_stream.set_nodelay(true)?;

        let mut stream = if let Some(ref pwd) = self.password {
            StreamWrapper::Encrypted(crate::auth::perform_handshake(tcp_stream, pwd)?)
        } else {
            StreamWrapper::Plain(tcp_stream)
        };

        stream.write_all(path.as_bytes())?;
        if let Some(p) = payload {
            stream.write_all(b" ")?;
            stream.write_all(p.as_bytes())?;
        }
        stream.write_all(b"\0")?;

        let mut buf = Vec::new();
        stream.read_to_end(&mut buf)?;

        let response = String::from_utf8(buf)
            .map_err(|_| ViiperError::UnexpectedResponse("invalid UTF-8".into()))?
            .trim_end_matches('\n')
            .to_string();

        if response.starts_with("{\"status\":") {
            let problem: ProblemJson = serde_json::from_str(&response)?;
            return Err(ViiperError::Protocol(problem));
        }

        serde_json::from_str(&response).map_err(Into::into)
    }
{{range .Routes}}{{if eq .Method "Register"}}
    /// {{.Handler}}: {{.Path}}{{if .ResponseDTO}} -> {{.ResponseDTO}}{{end}}
    pub fn {{toSnakeCase .Handler}}(&self{{generateMethodParamsRust .}}) -> Result<{{if .ResponseDTO}}{{.ResponseDTO}}{{else}}(){{end}}, ViiperError> {
        let path = {{generatePathRust .}};
        {{generatePayloadRust .}}
        {{if .ResponseDTO}}self.do_request(&path, payload.as_deref()){{else}}self.do_request::<serde_json::Value>(&path, payload.as_deref())?;
        Ok(()){{end}}
    }
{{end}}{{end}}
    /// Connect to a device stream for sending input and receiving output.
    pub fn connect_device(&self, bus_id: u32, dev_id: &str) -> Result<DeviceStream, ViiperError> {
        DeviceStream::connect(self.addr, bus_id, dev_id, self.password.as_deref())
    }
}

/// A connected device stream for bidirectional communication.
pub struct DeviceStream {
    stream: StreamWrapper,
    output_thread: Option<std::thread::JoinHandle<()>>,
    disconnect_callback: Option<Box<dyn FnOnce() + Send + 'static>>,
}

impl DeviceStream {
    pub fn connect(addr: SocketAddr, bus_id: u32, dev_id: &str, password: Option<&str>) -> Result<Self, ViiperError> {
        let tcp_stream = TcpStream::connect(addr)?;
		tcp_stream.set_nodelay(true)?;
		
		let mut stream = if let Some(pwd) = password {
		    StreamWrapper::Encrypted(crate::auth::perform_handshake(tcp_stream, pwd)?)
		} else {
		    StreamWrapper::Plain(tcp_stream)
		};
		
		let handshake = format!("bus/{}/{}\0", bus_id, dev_id);
        stream.write_all(handshake.as_bytes())?;
        Ok(Self { 
            stream,
            output_thread: None,
            disconnect_callback: None,
        })
    }

    /// Send a device input to the device.
    pub fn send<T: crate::wire::DeviceInput>(&mut self, input: &T) -> Result<(), ViiperError> {
        let bytes = input.to_bytes();
        self.stream.write_all(&bytes)?;
        Ok(())
    }

    /// Register a callback to receive device output asynchronously.
    /// The callback receives a BufRead reader and must read the exact number of bytes expected.
    /// The callback will be invoked repeatedly on a background thread until it returns an error.
    /// Only one callback can be registered at a time.
    pub fn on_output<F>(&mut self, mut callback: F) -> Result<(), ViiperError>
    where
        F: FnMut(&mut dyn std::io::BufRead) -> std::io::Result<()> + Send + 'static,
    {
        if self.output_thread.is_some() {
            return Err(ViiperError::UnexpectedResponse("Output callback already registered".into()));
        }

        let stream = self.stream.try_clone()?;
        let disconnect = self.disconnect_callback.take();
        let handle = std::thread::spawn(move || {
            let mut reader = std::io::BufReader::new(stream);
            while callback(&mut reader).is_ok() {}
            if let Some(on_disconnect) = disconnect {
                on_disconnect();
            }
        });
        self.output_thread = Some(handle);
        Ok(())
    }

    pub fn on_disconnect<F>(&mut self, callback: F) -> Result<(), ViiperError>
    where
        F: FnOnce() + Send + 'static,
    {
        self.disconnect_callback = Some(Box::new(callback));
        Ok(())
    }

    /// Send raw bytes to the device.
    pub fn send_raw(&mut self, data: &[u8]) -> Result<(), ViiperError> {
        self.stream.write_all(data)?;
        Ok(())
    }

    /// Read raw bytes from the device.
    pub fn read_raw(&mut self, buf: &mut [u8]) -> Result<usize, ViiperError> {
        self.stream.read(buf).map_err(Into::into)
    }

    /// Read exact number of bytes from the device.
    pub fn read_exact(&mut self, buf: &mut [u8]) -> Result<(), ViiperError> {
        self.stream.read_exact(buf).map_err(Into::into)
    }
}

impl Drop for DeviceStream {
    fn drop(&mut self) {
        let _ = self.stream.shutdown(std::net::Shutdown::Both);
        if let Some(handle) = self.output_thread.take() {
            let _ = handle.join();
        }
    }
}
`

func generateClient(logger *slog.Logger, srcDir string, md *meta.Metadata) error {
	logger.Debug("Generating client.rs (sync API)")
	outputFile := filepath.Join(srcDir, "client.rs")

	funcMap := template.FuncMap{
		"toSnakeCase":              common.ToSnakeCase,
		"generateMethodParamsRust": generateMethodParamsRust,
		"generatePathRust":         generatePathRust,
		"generatePayloadRust":      generatePayloadRust,
	}

	tmpl, err := template.New("client").Funcs(funcMap).Parse(clientTemplate)
	if err != nil {
		return fmt.Errorf("parse template: %w", err)
	}

	f, err := os.Create(outputFile)
	if err != nil {
		return fmt.Errorf("create file: %w", err)
	}
	defer f.Close() //nolint:errcheck

	data := struct {
		Header string
		Routes []scanner.RouteInfo
	}{
		Header: writeFileHeaderRust(),
		Routes: md.Routes,
	}

	if err := tmpl.Execute(f, data); err != nil {
		return fmt.Errorf("execute template: %w", err)
	}

	logger.Info("Generated client.rs", "file", outputFile)
	return nil
}
