package api

import (
	"context"
	"io"
	"log/slog"
	"net"
	"sync/atomic"
	"testing"
	"time"

	usbserver "github.com/Alia5/VIIPER/internal/server/usb"
	usbmodel "github.com/Alia5/VIIPER/usb"
	"github.com/Alia5/VIIPER/virtualbus"
)

type auditTimerPad struct{ descriptor usbmodel.Descriptor }

func (p *auditTimerPad) GetDescriptor() *usbmodel.Descriptor                           { return &p.descriptor }
func (p *auditTimerPad) GetDeviceSpecificArgs() map[string]any                         { return nil }
func (p *auditTimerPad) HandleTransfer(context.Context, uint32, uint32, []byte) []byte { return nil }

type auditLocalPipe struct{ net.Conn }

func (auditLocalPipe) RemoteAddr() net.Addr {
	return &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 37999}
}

// Diagnostic only. Simulate old stream teardown finishing after its replacement
// is already active. The short timeout scales the real 60s cleanup policy.
func TestRetiredStreamCannotDeleteLiveReplacementDevice(t *testing.T) {
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	usbs := usbserver.New(usbserver.ServerConfig{}, logger, nil)
	bus := virtualbus.New(380)
	pad := &auditTimerPad{descriptor: usbmodel.Descriptor{Device: usbmodel.DeviceDescriptor{IDVendor: 0x057e, IDProduct: 0x2009}}}
	devctx, err := bus.Add(pad)
	if err != nil {
		t.Fatal(err)
	}
	if err = usbs.AddBus(bus); err != nil {
		t.Fatal(err)
	}
	defer usbs.RemoveBus(380)
	srv := New(usbs, "", ServerConfig{DeviceHandlerConnectTimeout: 100 * time.Millisecond}, logger)
	entered := make(chan int32, 2)
	oldExit, newExit := make(chan struct{}), make(chan struct{})
	var number atomic.Int32
	var replacementActive atomic.Bool
	srv.Router().RegisterStream("bus/{busId}/{deviceid}", func(conn net.Conn, _ *usbmodel.Device, _ *slog.Logger) error {
		index := number.Add(1)
		if index == 2 {
			replacementActive.Store(true)
			defer replacementActive.Store(false)
		}
		entered <- index
		if index == 1 {
			<-oldExit
		} else {
			<-newExit
		}
		return nil
	})
	open := func() (net.Conn, <-chan struct{}) {
		client, peer := net.Pipe()
		done := make(chan struct{})
		go func() { defer close(done); srv.handleConn(auditLocalPipe{peer}) }()
		_ = client.SetWriteDeadline(time.Now().Add(2 * time.Second))
		if _, err := client.Write([]byte("bus/380/1\x00")); err != nil {
			t.Fatal(err)
		}
		select {
		case <-entered:
		case <-time.After(2 * time.Second):
			t.Fatal("stream failed to open")
		}
		return client, done
	}
	oldClient, oldDone := open()
	defer oldClient.Close()
	newClient, newDone := open()
	defer newClient.Close()
	defer func() { close(newExit); <-newDone }()
	if !replacementActive.Load() {
		t.Fatal("replacement not active")
	}
	close(oldExit)
	select {
	case <-oldDone:
	case <-time.After(2 * time.Second):
		t.Fatal("old stream failed to retire")
	}
	select {
	case <-devctx.Done():
		t.Fatal("old stream deleted device while replacement is active")
	case <-time.After(350 * time.Millisecond):
		if !replacementActive.Load() {
			t.Fatal("replacement stopped unexpectedly")
		}
	}

}

func TestReplacementCancelsAlreadyArmedCleanup(t *testing.T) {
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	usbs := usbserver.New(usbserver.ServerConfig{}, logger, nil)
	bus := virtualbus.New(381)
	pad := &auditTimerPad{}
	devctx, err := bus.Add(pad)
	if err != nil {
		t.Fatal(err)
	}
	if err = usbs.AddBus(bus); err != nil {
		t.Fatal(err)
	}
	defer usbs.RemoveBus(381)
	srv := New(usbs, "", ServerConfig{DeviceHandlerConnectTimeout: 200 * time.Millisecond}, logger)
	entered := make(chan struct{}, 2)
	exitNew := make(chan struct{})
	var number atomic.Int32
	srv.Router().RegisterStream("bus/{busId}/{deviceid}", func(net.Conn, *usbmodel.Device, *slog.Logger) error {
		index := number.Add(1)
		entered <- struct{}{}
		if index == 2 {
			<-exitNew
		}
		return nil
	})
	open := func() (net.Conn, <-chan struct{}) {
		client, peer := net.Pipe()
		done := make(chan struct{})
		go func() { defer close(done); srv.handleConn(auditLocalPipe{peer}) }()
		_ = client.SetWriteDeadline(time.Now().Add(2 * time.Second))
		if _, err := client.Write([]byte("bus/381/1\x00")); err != nil {
			t.Fatal(err)
		}
		select {
		case <-entered:
		case <-time.After(2 * time.Second):
			t.Fatal("stream failed to open")
		}
		return client, done
	}
	oldClient, oldDone := open()
	defer oldClient.Close()
	select {
	case <-oldDone:
	case <-time.After(2 * time.Second):
		t.Fatal("old stream did not finish")
	}
	newClient, newDone := open()
	defer newClient.Close()
	defer func() { close(exitNew); <-newDone }()
	select {
	case <-devctx.Done():
		t.Fatal("control invalid: a normally ordered replacement was removed")
	case <-time.After(450 * time.Millisecond):
		t.Log("AUDIT_CONTROL normal_teardown_then_replacement=true virtual_device_preserved=true driver_used=false")
	}
}
