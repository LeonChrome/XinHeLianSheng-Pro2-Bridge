package nspro

import (
	"bytes"
	"context"
	"encoding/binary"
	"github.com/Alia5/VIIPER/device"
	"github.com/Alia5/VIIPER/device/ns2pro"
	"github.com/Alia5/VIIPER/usbip"
	"math"
	"testing"
	"time"
)

func pad(t *testing.T) *Pro {
	t.Helper()
	p, e := New(nil)
	if e != nil {
		t.Fatal(e)
	}
	return p
}
func read(t *testing.T, p *Pro) []byte {
	t.Helper()
	ctx, c := context.WithTimeout(context.Background(), time.Second)
	defer c()
	r := p.HandleTransfer(ctx, 1, usbip.DirIn, nil)
	if r == nil {
		t.Fatal("report timeout")
	}
	return r
}
func command(t *testing.T, p *Pro, id byte, body ...byte) []byte {
	t.Helper()
	o := make([]byte, 11+len(body))
	o[0] = 1
	o[10] = id
	copy(o[11:], body)
	p.output(o)
	r := read(t, p)
	if r[0] != 0x21 || r[14] != id {
		t.Fatalf("wrong ACK %02x: %x", id, r)
	}
	return r
}
func noInput(t *testing.T, p *Pro) {
	t.Helper()
	ctx, c := context.WithTimeout(context.Background(), 2*time.Millisecond)
	defer c()
	if r := p.HandleTransfer(ctx, 1, usbip.DirIn, nil); r != nil {
		t.Fatal("fabricated/repeated input", r)
	}
}

func TestIdentityAndUSBHandshake(t *testing.T) {
	p := pad(t)
	d := p.GetDescriptor()
	if d.Device.IDVendor != 0x057E || d.Device.IDProduct != 0x2009 || d.Strings[2] != "Pro Controller" {
		t.Fatal("identity")
	}
	b, e := d.Interfaces[0].HID.ReportDescriptor.Bytes()
	if e != nil {
		t.Fatal(e)
	}
	for _, id := range []byte{0x21, 0x30, 0x3F, 0x81, 1, 0x10, 0x80} {
		if !bytes.Contains(b, []byte{0x85, id}) {
			t.Fatalf("missing report %02x", id)
		}
	}
	q, e := New(&device.CreateOptions{DeviceSpecific: `{"serial_number":"PRO2-NSPRO-S2"}`})
	if e != nil || q.mac == p.mac {
		t.Fatal("slot identity")
	}
	if _, e = New(&device.CreateOptions{DeviceSpecific: "bad"}); e == nil {
		t.Fatal("invalid JSON accepted")
	}
	read(t, p)
	for _, op := range []byte{1, 2, 3, 2} {
		p.output([]byte{0x80, op})
		r := read(t, p)
		if r[0] != 0x81 || r[1] != op {
			t.Fatal("USB ACK")
		}
		if op == 1 {
			if r[3] != 3 {
				t.Fatal("Pro type")
			}
			for i := 0; i < 6; i++ {
				if r[4+i] != p.mac[5-i] {
					t.Fatal("MAC order")
				}
			}
		}
	}
	p.output([]byte{0x80, 4})
	if read(t, p)[0] != 0x30 {
		t.Fatal("force USB")
	}
	noInput(t, p)
	r := command(t, p, 2)
	if r[13] != 0x82 || r[17] != 3 || !bytes.Equal(r[19:25], p.mac[:]) {
		t.Fatal("device info")
	}
}

func TestFreshFramesButtonsAndIMU(t *testing.T) {
	p := pad(t)
	read(t, p)
	command(t, p, 3, 0x30)
	command(t, p, 0x40, 1)
	for i := 0; i < 1334; i++ {
		s := *ns2pro.NewInputState()
		s.LX = uint16(i)
		s.GyroX = 16384
		s.AccelZ = 4096
		s.Buttons = ns2pro.ButtonA | ns2pro.ButtonUp | ns2pro.ButtonGL
		p.UpdateInputState(s)
		r := read(t, p)
		if r[0] != 0x30 || r[3] != 8 || r[4] != 0 || r[5] != 2 || uint16(r[6])|(uint16(r[7]&15)<<8) != uint16(i) {
			t.Fatalf("input mapping %d", i)
		}
		for slot := 0; slot < 3; slot++ {
			o := 13 + slot*12
			if binary.LittleEndian.Uint16(r[o+4:]) != 4096 || math.Abs(float64(int16(binary.LittleEndian.Uint16(r[o+6:])))-13371.0/936*1000) > 1 {
				t.Fatal("calibrated IMU gain")
			}
		}
	}
	noInput(t, p)
	for i := 0; i < 1000; i++ {
		s := *ns2pro.NewInputState()
		s.LX = uint16(i)
		p.UpdateInputState(s)
	}
	r := read(t, p)
	if uint16(r[6])|(uint16(r[7]&15)<<8) != 999 {
		t.Fatal("freshest coalescing")
	}
	noInput(t, p)
	command(t, p, 3, 0x3F)
	s := *ns2pro.NewInputState()
	s.Buttons = ns2pro.ButtonA | ns2pro.ButtonZL
	s.LX = 0
	s.RX = 4095
	p.UpdateInputState(s)
	r = read(t, p)
	if len(r) != 12 || r[0] != 0x3F || r[1] != 0x42 || r[3] != 8 || binary.LittleEndian.Uint16(r[4:]) != 0 || binary.LittleEndian.Uint16(r[8:]) != 65535 {
		t.Fatal("simple HID mapping")
	}
}

func TestFlashBoundsAndControl(t *testing.T) {
	p := pad(t)
	read(t, p)
	for _, a := range []uint32{0x6020, 0x603D, 0x6046, 0x6050, 0x6080, 0x6098, 0x8010, 0x8026, 0xFFFFFFFF} {
		var b [5]byte
		binary.LittleEndian.PutUint32(b[:], a)
		b[4] = 255
		r := command(t, p, 0x10, b[:]...)
		if r[13] != 0x90 || r[19] != 29 || binary.LittleEndian.Uint32(r[15:19]) != a {
			t.Fatal("SPI framing")
		}
		for i := 0; i < 29; i++ {
			if r[20+i] != flashByte(a+uint32(i)) {
				t.Fatal("SPI offsets")
			}
		}
	}
	if flashByte(0x8010) != 255 || flashByte(0x8026) != 255 {
		t.Fatal("invented user calibration")
	}
	if command(t, p, 0x10)[13] != 0 || command(t, p, 0x11)[13] != 0 {
		t.Fatal("malformed/read-only flash")
	}
	data := make([]byte, 63)
	data[0] = 1
	data[9] = 2
	if _, ok := p.HandleControl(0x21, 9, 0x0201, 0, 64, data); !ok || read(t, p)[14] != 2 {
		t.Fatal("stripped ID, counter equals report ID")
	}
	for _, id := range []byte{0x30, 0x3F} {
		r, ok := p.HandleControl(0xA1, 1, 0x0100|uint16(id), 0, 4, nil)
		if !ok || len(r) != 4 || r[0] != id {
			t.Fatal("GET_REPORT length")
		}
	}
	for _, bad := range [][]byte{nil, {1}, {1, 0}, {0x10, 0}, {1, 0, 0, 0, 0, 0, 0, 0, 0}, {0xFF, 1}} {
		p.output(bad)
	}
	noInput(t, p)
	for i := 0; i < 200; i++ {
		p.output([]byte{1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2})
	}
	if len(p.replies) != 64 {
		t.Fatal("unbounded reply queue")
	}
	for i := 0; i < 64; i++ {
		if read(t, p)[0] != 0x21 {
			t.Fatal("ACK starvation")
		}
	}
}

func TestStereoRumbleAndLifetime(t *testing.T) {
	p := pad(t)
	read(t, p)
	var got [FeedbackSize]byte
	calls := 0
	clear1 := p.SetOutputCallback(func(f [FeedbackSize]byte) { got = f; calls++ })
	command(t, p, 0x48, 1)
	out := []byte{0x10, 0, 0, 0xC8, 0x40, 0x72, 0, 0x64, 0x40, 0x59}
	p.output(out)
	if calls != 1 || !bytes.Equal(got[:8], out[2:10]) || got[8] != 1 {
		t.Fatal("stereo HD feedback lost")
	}
	command(t, p, 0x30, 5)
	if got[8] != 3 || got[9] != 5 {
		t.Fatal("LED flags")
	}
	command(t, p, 0x48, 0)
	if got[8] != 1 || !bytes.Equal(got[:8], make([]byte, 8)) {
		t.Fatal("missing stop")
	}
	n := calls
	p.output(out)
	if calls != n {
		t.Fatal("disabled rumble leaked")
	}
	clear2 := p.SetOutputCallback(func(f [FeedbackSize]byte) { calls += 10 })
	clear1()
	command(t, p, 0x48, 0)
	if calls != n+10 {
		t.Fatal("stale callback cleanup")
	}
	clear2()
	command(t, p, 0x48, 0)
	if calls != n+10 {
		t.Fatal("disposed callback")
	}
}
