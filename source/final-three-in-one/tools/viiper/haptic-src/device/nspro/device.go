package nspro

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"math"
	"sync"
	"time"

	"github.com/Alia5/VIIPER/device"
	"github.com/Alia5/VIIPER/device/ns2pro"
	"github.com/Alia5/VIIPER/usb"
	"github.com/Alia5/VIIPER/usbip"
)

const FeedbackSize = 10 // original 4-byte HD rumble per side + flags + player LEDs

type MetaState struct {
	SerialNumber string `json:"serial_number"`
}

type Pro struct {
	mu              sync.Mutex
	wake            chan struct{}
	state           ns2pro.InputState // shared 28-byte internal wire, not a Pro2 USB identity
	pending         bool
	replies         [][]byte
	mode            byte
	imu, vibration  bool
	led             byte
	mac             [6]byte
	started         time.Time
	descriptor      usb.Descriptor
	meta            MetaState
	feedback        func([FeedbackSize]byte)
	feedbackVersion uint64
}

func New(o *device.CreateOptions) (*Pro, error) {
	meta := MetaState{SerialNumber: "PRO2-NSPRO-S1"}
	if o != nil && o.DeviceSpecific != "" {
		if err := json.Unmarshal([]byte(o.DeviceSpecific), &meta); err != nil {
			return nil, fmt.Errorf("nspro metadata: %w", err)
		}
	}
	if meta.SerialNumber == "" {
		meta.SerialNumber = "PRO2-NSPRO-S1"
	}
	// A stable locally administered address; distinct slots have distinct identities.
	mac := [6]byte{0x02, 0x50, 0x52, 0x4F, 0x31, 0}
	for _, b := range []byte(meta.SerialNumber) {
		mac[5] = mac[5]*31 + b
	}
	p := &Pro{wake: make(chan struct{}, 1), state: *ns2pro.NewInputState(), pending: true,
		mode: 0x3F, mac: mac, started: time.Now(), meta: meta, descriptor: makeDescriptor(meta.SerialNumber)}
	return p, nil
}

func (p *Pro) GetDescriptor() *usb.Descriptor { return &p.descriptor }
func (p *Pro) GetDeviceSpecificArgs() map[string]any {
	p.mu.Lock()
	defer p.mu.Unlock()
	return map[string]any{"serial_number": p.meta.SerialNumber, "source_paced": true}
}
func (p *Pro) UpdateInputState(s ns2pro.InputState) {
	p.mu.Lock()
	p.state = s
	p.pending = true
	p.mu.Unlock()
	p.signal()
}
func (p *Pro) signal() {
	select {
	case p.wake <- struct{}{}:
	default:
	}
}
func (p *Pro) SetOutputCallback(f func([FeedbackSize]byte)) func() {
	p.mu.Lock()
	p.feedbackVersion++
	version := p.feedbackVersion
	p.feedback = f
	p.mu.Unlock()
	return func() {
		p.mu.Lock()
		if p.feedbackVersion == version {
			p.feedback = nil
		}
		p.mu.Unlock()
	}
}

func (p *Pro) HandleTransfer(ctx context.Context, ep, dir uint32, out []byte) []byte {
	if ep != 1 {
		return nil
	}
	if dir == usbip.DirOut {
		p.output(out)
		return nil
	}
	if dir != usbip.DirIn {
		return nil
	}
	for {
		if ctx.Err() != nil {
			return nil
		}
		p.mu.Lock()
		// Command replies must not wait for a physical input or be coalesced with it.
		if len(p.replies) > 0 {
			r := p.replies[0]
			p.replies = p.replies[1:]
			p.mu.Unlock()
			return r
		}
		if p.pending {
			p.pending = false
			r := p.inputLocked(p.mode)
			p.mu.Unlock()
			return r
		}
		p.mu.Unlock()
		select {
		case <-ctx.Done():
			return nil
		case <-p.wake:
		}
	}
}

func (p *Pro) HandleControl(bm, request uint8, value, index, length uint16, data []byte) ([]byte, bool) {
	if bm == 0x21 && request == 0x09 && byte(value>>8) == 2 {
		id := byte(value)
		if len(data) == 0 || len(data) == 63 || data[0] != id {
			data = append([]byte{id}, data...)
		}
		p.output(data)
		return nil, true
	}
	if bm == 0xA1 && request == 0x01 && byte(value>>8) == 1 {
		p.mu.Lock()
		defer p.mu.Unlock()
		id := byte(value)
		if id == 0 {
			id = p.mode
		}
		if id != 0x30 && id != 0x3F {
			return nil, false
		}
		r := p.inputLocked(id)
		if int(length) < len(r) {
			r = r[:length]
		}
		return r, true
	}
	if bm == 0x21 && (request == 0x0A || request == 0x0B) {
		return nil, true
	}
	return nil, false
}

func packStick(b []byte, x, y uint16) {
	x = min(x, 4095)
	y = min(y, 4095)
	b[0] = byte(x)
	b[1] = byte((x >> 8) | (y << 4))
	b[2] = byte(y >> 4)
}
func (p *Pro) standardLocked(r []byte) {
	s := p.state
	r[1] = byte(time.Since(p.started).Microseconds() / 5000)
	r[2] = 0x81 // virtual USB connection, not a claimed physical battery reading
	// The internal bits are those of ns2pro; the USB bytes below are Switch 1.
	for _, entry := range []struct {
		bit    uint32
		offset int
		mask   byte
	}{
		{ns2pro.ButtonY, 3, 1}, {ns2pro.ButtonX, 3, 2}, {ns2pro.ButtonB, 3, 4}, {ns2pro.ButtonA, 3, 8},
		{ns2pro.ButtonR, 3, 0x40}, {ns2pro.ButtonZR, 3, 0x80},
		{ns2pro.ButtonMinus, 4, 1}, {ns2pro.ButtonPlus, 4, 2}, {ns2pro.ButtonRightStick, 4, 4},
		{ns2pro.ButtonLeftStick, 4, 8}, {ns2pro.ButtonHome, 4, 0x10}, {ns2pro.ButtonCapture, 4, 0x20},
		{ns2pro.ButtonDown, 5, 1}, {ns2pro.ButtonUp, 5, 2}, {ns2pro.ButtonRight, 5, 4},
		{ns2pro.ButtonLeft, 5, 8}, {ns2pro.ButtonL, 5, 0x40}, {ns2pro.ButtonZL, 5, 0x80},
	} {
		if s.Buttons&entry.bit != 0 {
			r[entry.offset] |= entry.mask
		}
	}
	packStick(r[6:9], s.LX, s.LY)
	packStick(r[9:12], s.RX, s.RY)
	r[12] = 8
}

func gyro(raw int16) int16 {
	// Matches the exposed SPI calibration: 936 dps / 13371 counts.
	// The Windows parser keeps Pro2 samples at 16.384 counts/dps, not the
	// older firmware's 14.247 source scale.
	return int16(max(-32768, min(32767, math.Round(float64(raw)/16.384*(13371.0/936.0)))))
}
func (p *Pro) inputLocked(id byte) []byte {
	if id == 0x3F {
		return p.simpleLocked()
	}
	r := make([]byte, 64)
	r[0] = 0x30
	p.standardLocked(r)
	if p.imu {
		s := p.state
		// Switch 1 requires three slots. They hold the latest measured FD2 sample;
		// this is not three new physical samples and does not inflate input Hz.
		for slot := 0; slot < 3; slot++ {
			for axis, value := range []int16{s.AccelX, s.AccelY, s.AccelZ, gyro(s.GyroX), gyro(s.GyroY), gyro(s.GyroZ)} {
				binary.LittleEndian.PutUint16(r[13+slot*12+axis*2:], uint16(value))
			}
		}
	}
	return r
}
func (p *Pro) simpleLocked() []byte {
	s := p.state
	r := make([]byte, 12)
	r[0] = 0x3F
	for i, b := range []uint32{ns2pro.ButtonB, ns2pro.ButtonA, ns2pro.ButtonY, ns2pro.ButtonX, ns2pro.ButtonL, ns2pro.ButtonR, ns2pro.ButtonZL, ns2pro.ButtonZR,
		ns2pro.ButtonMinus, ns2pro.ButtonPlus, ns2pro.ButtonLeftStick, ns2pro.ButtonRightStick, ns2pro.ButtonHome, ns2pro.ButtonCapture} {
		if s.Buttons&b != 0 {
			r[1+i/8] |= 1 << uint(i%8)
		}
	}
	r[3] = 8
	u, rr, d, l := s.Buttons&ns2pro.ButtonUp != 0, s.Buttons&ns2pro.ButtonRight != 0, s.Buttons&ns2pro.ButtonDown != 0, s.Buttons&ns2pro.ButtonLeft != 0
	switch {
	case u && rr:
		r[3] = 1
	case rr && d:
		r[3] = 3
	case d && l:
		r[3] = 5
	case l && u:
		r[3] = 7
	case u:
		r[3] = 0
	case rr:
		r[3] = 2
	case d:
		r[3] = 4
	case l:
		r[3] = 6
	}
	for i, v := range []uint16{s.LX, s.LY, s.RX, s.RY} {
		v = min(v, 4095)
		n := uint32(v) * 32768 / 2048
		if v > 2048 {
			n = 32768 + uint32(v-2048)*32767/2047
		}
		binary.LittleEndian.PutUint16(r[4+i*2:], uint16(n))
	}
	return r
}

func (p *Pro) output(out []byte) {
	if len(out) < 2 {
		return
	}
	p.mu.Lock()
	var feedback [FeedbackSize]byte
	if out[0] == 0x80 {
		if out[1] == 4 {
			p.mode = 0x30
			p.pending = true
		} else {
			r := make([]byte, 64)
			r[0] = 0x81
			r[1] = out[1]
			if out[1] == 1 {
				r[3] = 3
				for i := 0; i < 6; i++ {
					r[4+i] = p.mac[5-i]
				}
			}
			p.enqueueLocked(r)
		}
	} else if (out[0] == 1 || out[0] == 0x10) && len(out) >= 10 {
		if p.vibration {
			copy(feedback[:8], out[2:10])
			feedback[8] = 1
		}
		if out[0] == 1 && len(out) >= 11 {
			command := out[10]
			r := make([]byte, 64)
			r[0] = 0x21
			p.standardLocked(r)
			r[13] = 0x80
			r[14] = command
			switch command {
			case 2:
				r[13] = 0x82
				copy(r[15:], []byte{3, 0x48, 3, 2})
				copy(r[19:25], p.mac[:])
				r[25] = 1
				r[26] = 2
			case 3:
				if len(out) > 11 && (out[11] == 0x30 || out[11] == 0x3F) {
					p.mode = out[11]
				}
			case 0x10:
				p.spiReplyLocked(out, r)
			case 0x30:
				if len(out) > 11 {
					p.led = out[11]
					feedback[8] |= 2
					feedback[9] = p.led
				}
			case 0x31:
				r[13] = 0xB0
				r[15] = p.led
			case 0x40:
				if len(out) > 11 {
					p.imu = out[11] != 0
				}
			case 0x48:
				if len(out) > 11 {
					p.vibration = out[11] != 0
					if !p.vibration {
						feedback = [FeedbackSize]byte{}
						feedback[8] = 1
					}
				}
			case 0x50:
				r[13] = 0xD0
				binary.LittleEndian.PutUint16(r[15:], 3800*2/5)
			case 0x10 + 1, 0x10 + 2:
				r[13] = 0 // virtual calibration flash is read-only
			default: // harmless commands acknowledge without touching the physical controller
			}
			p.enqueueLocked(r)
		}
	}
	callback := p.feedback
	p.mu.Unlock()
	p.signal()
	if callback != nil && feedback[8] != 0 {
		callback(feedback)
	}
}
func (p *Pro) enqueueLocked(r []byte) {
	// Host commands are serialized by SDL/Steam; bound malicious clients as well.
	if len(p.replies) >= 64 {
		p.replies = p.replies[1:]
	}
	p.replies = append(p.replies, r)
}

func (p *Pro) spiReplyLocked(out, r []byte) {
	if len(out) < 16 {
		r[13] = 0
		return
	}
	address := binary.LittleEndian.Uint32(out[11:15])
	size := min(int(out[15]), 29)
	r[13] = 0x90
	copy(r[15:19], out[11:15])
	r[19] = byte(size)
	for i := 0; i < size; i++ {
		r[20+i] = flashByte(address + uint32(i))
	}
}
func flashByte(address uint32) byte {
	// Centre 2048, symmetric 1600 ranges match the app's normalized stick range.
	stick := []byte{0x40, 0x06, 0x64, 0, 0x08, 0x80, 0x40, 0x06, 0x64, 0, 0x08, 0x80, 0x40, 0x06, 0x64, 0x40, 0x06, 0x64}
	if address >= 0x603D && address < 0x603D+18 {
		return stick[address-0x603D]
	}
	if address >= 0x6020 && address < 0x6020+24 {
		var calibration [24]byte
		for i := 0; i < 3; i++ {
			binary.LittleEndian.PutUint16(calibration[6+i*2:], 16384)
			binary.LittleEndian.PutUint16(calibration[18+i*2:], 13371)
		}
		return calibration[address-0x6020]
	}
	if address == 0x6012 {
		return 3
	} // Pro Controller type
	if address >= 0x6050 && address < 0x605C {
		if address < 0x6053 {
			return 0x32
		}
		return 0xFF
	}
	if address >= 0x6080 && address < 0x6098 {
		params := []byte{0x5E, 1, 0, 0, 0xF1, 0x0F, 0x19, 0xD0, 0x4C, 0xAE, 0x40, 0xE1, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF}
		return params[address-0x6080]
	}
	if address >= 0x6098 && address < 0x60AA {
		params := []byte{0x19, 0xD0, 0x4C, 0xAE, 0x40, 0xE1, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF}
		return params[address-0x6098]
	}
	return 0xFF // absent user calibration magic (never fabricate a physical calibration)
}
