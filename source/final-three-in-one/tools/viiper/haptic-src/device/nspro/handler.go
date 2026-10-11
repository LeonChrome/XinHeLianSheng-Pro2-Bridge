package nspro

import (
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net"

	"github.com/Alia5/VIIPER/device"
	"github.com/Alia5/VIIPER/device/ns2pro"
	"github.com/Alia5/VIIPER/internal/server/api"
	"github.com/Alia5/VIIPER/usb"
)

func init() { api.RegisterDevice("nspro", &handler{}) }

type handler struct{}

func (h *handler) CreateDevice(o *device.CreateOptions) (usb.Device, error) { return New(o) }
func (h *handler) StreamHandler() api.StreamHandlerFunc {
	return func(conn net.Conn, dev *usb.Device, logger *slog.Logger) error {
		if dev == nil || *dev == nil {
			return fmt.Errorf("nil nspro device")
		}
		p, ok := (*dev).(*Pro)
		if !ok {
			return fmt.Errorf("%w: expected nspro", device.ErrWrongDeviceType)
		}
		clear := p.SetOutputCallback(func(f [FeedbackSize]byte) {
			if _, err := conn.Write(f[:]); err != nil {
				logger.Debug("nspro feedback connection closed", "error", err)
			}
		})
		defer clear()
		buf := make([]byte, ns2pro.InputWireSize)
		for {
			if _, err := io.ReadFull(conn, buf); err != nil {
				if err == io.EOF {
					return nil
				}
				return err
			}
			var s ns2pro.InputState
			if err := s.UnmarshalBinary(buf); err != nil {
				return err
			}
			p.UpdateInputState(s)
		}
	}
}
func (h *handler) UpdateMetaState(meta string, dev *usb.Device) error {
	if dev == nil || *dev == nil {
		return fmt.Errorf("nil nspro device")
	}
	p, ok := (*dev).(*Pro)
	if !ok {
		return fmt.Errorf("%w: expected nspro", device.ErrWrongDeviceType)
	}
	var m MetaState
	if err := json.Unmarshal([]byte(meta), &m); err != nil {
		return err
	}
	p.mu.Lock()
	defer p.mu.Unlock()
	if m.SerialNumber != "" && m.SerialNumber != p.meta.SerialNumber {
		return fmt.Errorf("nspro serial cannot change while attached")
	}
	return nil
}
