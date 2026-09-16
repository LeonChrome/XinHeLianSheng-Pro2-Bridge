package dualsense

import (
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net"
	"sync"

	"github.com/Alia5/VIIPER/device"
	"github.com/Alia5/VIIPER/internal/server/api"
	"github.com/Alia5/VIIPER/usb"
)

const (
	hapticFrameHIDOutput = 1
	hapticFrameAudioPCM  = 2
)

func init() {
	api.RegisterDevice("dualsensehaptic", &hapticHandler{})
}

type hapticHandler struct{}

func (h *hapticHandler) CreateDevice(o *device.CreateOptions) (usb.Device, error) {
	if o == nil {
		o = &device.CreateOptions{}
	}
	d, err := new(o, false)
	if err != nil {
		return nil, err
	}
	d.descriptor = hapticDescriptor()
	d.deviceType = "dualsensehaptic"
	d.hapticEnabled = true
	return d, nil
}

func (h *hapticHandler) StreamHandler() api.StreamHandlerFunc {
	return func(conn net.Conn, devPtr *usb.Device, logger *slog.Logger) error {
		if devPtr == nil || *devPtr == nil {
			return fmt.Errorf("nil device")
		}
		ds, ok := (*devPtr).(*DualSense)
		if !ok {
			return fmt.Errorf("%w: expected DualSense haptic device", device.ErrWrongDeviceType)
		}

		var writeMu sync.Mutex
		var framesWritten uint64
		loggedKinds := make(map[byte]bool)
		writeFrame := func(kind byte, payload []byte) {
			if len(payload) > HapticAudioPacketSize {
				logger.Warn("haptic feedback payload truncated", "kind", kind, "length", len(payload))
				payload = payload[:HapticAudioPacketSize]
			}
			frame := make([]byte, HapticFeedbackFrameSize)
			frame[0] = kind
			binary.LittleEndian.PutUint16(frame[1:3], uint16(len(payload)))
			copy(frame[4:], payload)
			writeMu.Lock()
			defer writeMu.Unlock()
			if _, err := conn.Write(frame); err != nil {
				logger.Error("failed to send haptic feedback frame", "kind", kind, "error", err)
				return
			}
			framesWritten++
			if !loggedKinds[kind] {
				loggedKinds[kind] = true
				logger.Info(
					"haptic feedback stream is live",
					"kind", kind,
					"payloadBytes", len(payload),
					"wireBytes", len(frame),
				)
			}
		}

		ds.SetRawOutputCallback(func(report []byte) {
			writeFrame(hapticFrameHIDOutput, report)
		})
		ds.SetHapticAudioCallback(func(pcm []byte) {
			writeFrame(hapticFrameAudioPCM, pcm)
		})
		defer ds.SetRawOutputCallback(nil)
		defer ds.SetHapticAudioCallback(nil)

		buf := make([]byte, InputStateSize)
		for {
			if _, err := io.ReadFull(conn, buf); err != nil {
				if err == io.EOF {
					logger.Info("haptic client disconnected")
					return nil
				}
				return fmt.Errorf("read haptic input state: %w", err)
			}
			var state InputState
			if err := state.UnmarshalBinary(buf); err != nil {
				return fmt.Errorf("unmarshal haptic input state: %w", err)
			}
			ds.UpdateInputState(&state)
		}
	}
}

func (h *hapticHandler) UpdateMetaState(meta string, dev *usb.Device) error {
	ds, ok := (*dev).(*DualSense)
	if !ok {
		return fmt.Errorf("%w: expected DualSense haptic device", device.ErrWrongDeviceType)
	}
	ds.mtx.Lock()
	current := *ds.metaState
	ds.mtx.Unlock()
	if err := json.Unmarshal([]byte(meta), &current); err != nil {
		return fmt.Errorf("unmarshal meta state: %w", err)
	}
	ds.SetMetaState(current)
	return nil
}
