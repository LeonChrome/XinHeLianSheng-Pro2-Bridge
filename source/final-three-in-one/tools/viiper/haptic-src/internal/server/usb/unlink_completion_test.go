package usb

import (
	"context"
	"encoding/binary"
	"io"
	"log/slog"
	"net"
	"testing"
	"time"

	usbmodel "github.com/Alia5/VIIPER/usb"
	"github.com/Alia5/VIIPER/usbip"
	"github.com/Alia5/VIIPER/virtualbus"
)

type cancellableReadPad struct {
	descriptor usbmodel.Descriptor
	entered    chan struct{}
	complete   chan struct{}
}

func (p *cancellableReadPad) GetDescriptor() *usbmodel.Descriptor   { return &p.descriptor }
func (p *cancellableReadPad) GetDeviceSpecificArgs() map[string]any { return nil }
func (p *cancellableReadPad) SourcePacedInput() bool                { return true }
func (p *cancellableReadPad) HandleTransfer(ctx context.Context, ep, dir uint32, data []byte) []byte {
	if ep != 4 || dir != usbip.DirIn {
		return nil
	}
	p.entered <- struct{}{}
	select {
	case <-ctx.Done():
		return nil
	case <-p.complete:
		return make([]byte, 64)
	}
}

// Exercise the real USB/IP parser and async completion using only net.Pipe.
// A successful UNLINK must suppress that URB's RET_SUBMIT even when data wins
// the device callback race. A following EP0 reply is a deterministic barrier.
func TestUnlinkAndNormalCompletionAreMutuallyExclusive(t *testing.T) {
	server := New(ServerConfig{}, slog.New(slog.NewTextHandler(io.Discard, nil)), nil)
	bus := virtualbus.New(427)
	pad := &cancellableReadPad{entered: make(chan struct{}, 1), complete: make(chan struct{}, 1)}
	pad.descriptor = usbmodel.Descriptor{Device: usbmodel.DeviceDescriptor{Speed: 3}, Interfaces: []usbmodel.InterfaceConfig{{
		Endpoints: []usbmodel.EndpointDescriptor{{BEndpointAddress: 0x84, BMAttributes: 3, BInterval: 1}},
	}}}
	if _, err := bus.Add(pad); err != nil {
		t.Fatal(err)
	}
	if err := server.AddBus(bus); err != nil {
		t.Fatal(err)
	}
	client, peer := net.Pipe()
	done := make(chan error, 1)
	go func() { defer peer.Close(); done <- server.handleUrbStream(peer, pad) }()
	defer func() {
		client.Close()
		select {
		case <-done:
		case <-time.After(time.Second):
			t.Error("URB stream did not exit on client close")
		}
		bus.Remove(pad)
	}()
	write := func(command, seq, ep, unlink uint32) {
		t.Helper()
		var header [urbHdrSize]byte
		binary.BigEndian.PutUint32(header[0:], command)
		binary.BigEndian.PutUint32(header[4:], seq)
		binary.BigEndian.PutUint32(header[12:], usbip.DirIn)
		binary.BigEndian.PutUint32(header[16:], ep)
		if command == usbip.CmdUnlinkCode {
			binary.BigEndian.PutUint32(header[20:], unlink)
		} else {
			binary.BigEndian.PutUint32(header[24:], 64)
		}
		if _, err := client.Write(header[:]); err != nil {
			t.Fatal(err)
		}
	}
	read := func() (uint32, uint32, int32) {
		t.Helper()
		var header [urbHdrSize]byte
		if _, err := io.ReadFull(client, header[:]); err != nil {
			t.Fatal(err)
		}
		command := binary.BigEndian.Uint32(header[:])
		if command == usbip.RetSubmitCode {
			length := binary.BigEndian.Uint32(header[24:])
			if length > 64 {
				t.Fatalf("unexpected transfer length %d", length)
			}
			if _, err := io.CopyN(io.Discard, client, int64(length)); err != nil {
				t.Fatal(err)
			}
		}
		return command, binary.BigEndian.Uint32(header[4:]), int32(binary.BigEndian.Uint32(header[20:]))
	}
	for i := uint32(1); i <= 100; i++ {
		if err := client.SetDeadline(time.Now().Add(2 * time.Second)); err != nil {
			t.Fatal(err)
		}
		seq := i * 3
		write(usbip.CmdSubmitCode, seq, 4, 0)
		select {
		case <-pad.entered:
		case <-time.After(time.Second):
			t.Fatal("read was not pending")
		}
		if i%2 == 0 {
			pad.complete <- struct{}{}
		} // Race cancellation with a real completion.
		write(usbip.CmdUnlinkCode, seq+1, 4, seq)
		normal := false
		cmd, reply, status := read()
		if cmd == usbip.RetSubmitCode {
			if reply != seq {
				t.Fatalf("unrelated completion %d", reply)
			}
			normal = true
			cmd, reply, status = read()
		}
		if cmd != usbip.RetUnlinkCode || reply != seq+1 || normal && status != 0 || !normal && status != errConnReset {
			t.Fatalf("invalid completion/unlink pairing: normal=%v cmd=%d seq=%d status=%d", normal, cmd, reply, status)
		}
		write(usbip.CmdSubmitCode, seq+2, 0, 0)
		cmd, reply, _ = read()
		if cmd != usbip.RetSubmitCode || reply != seq+2 {
			t.Fatalf("cancelled URB completed after accepted UNLINK: %d/%d", cmd, reply)
		}
		// If cancellation won before the fake callback consumed its data, clear it.
		select {
		case <-pad.complete:
		default:
		}
	}
}
