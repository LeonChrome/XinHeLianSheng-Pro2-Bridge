package usb

import (
	"context"
	"encoding/binary"
	"fmt"
	"io"
	"log/slog"
	"net"
	"testing"
	"time"

	usbmodel "github.com/Alia5/VIIPER/usb"
	"github.com/Alia5/VIIPER/usbip"
	"github.com/Alia5/VIIPER/virtualbus"
)

type auditImportPad struct{ descriptor usbmodel.Descriptor }

func (p *auditImportPad) GetDescriptor() *usbmodel.Descriptor                           { return &p.descriptor }
func (p *auditImportPad) GetDeviceSpecificArgs() map[string]any                         { return nil }
func (p *auditImportPad) HandleTransfer(context.Context, uint32, uint32, []byte) []byte { return nil }

// Diagnostic only: net.Pipe exercises the release USB/IP server without any
// Windows driver, physical device, listener port, or changes to the application.
func TestOnlyOneLiveImportPerVirtualController(t *testing.T) {
	server := New(ServerConfig{ConnectionTimeout: 3 * time.Second}, slog.New(slog.NewTextHandler(io.Discard, nil)), nil)
	bus := virtualbus.New(379)
	pad := &auditImportPad{descriptor: usbmodel.Descriptor{
		Device:        usbmodel.DeviceDescriptor{Speed: 2, IDVendor: 0x057e, IDProduct: 0x2009, BNumConfigurations: 1},
		Configuration: usbmodel.ConfigurationDescriptor{BConfigurationValue: 1},
		Interfaces:    []usbmodel.InterfaceConfig{{Descriptor: usbmodel.InterfaceDescriptor{BInterfaceClass: 3}}},
	}}
	var err error
	if _, err = bus.Add(pad); err != nil {
		t.Fatal(err)
	}
	if err = server.AddBus(bus); err != nil {
		t.Fatal(err)
	}
	defer server.RemoveBus(379)

	clients := make([]net.Conn, 0, 5)
	finished := make([]chan error, 0, 5)
	defer func() {
		for _, client := range clients {
			_ = client.Close()
		}
		for _, done := range finished {
			select {
			case <-done:
			case <-time.After(3 * time.Second):
				t.Error("import connection did not stop after client close")
			}
		}
	}()

	accepted := 0
	for i := 1; i <= 5; i++ {
		client, peer := net.Pipe()
		clients = append(clients, client)
		done := make(chan error, 1)
		finished = append(finished, done)
		go func() { defer peer.Close(); done <- server.handleConn(peer) }()
		_ = client.SetDeadline(time.Now().Add(3 * time.Second))
		request := make([]byte, 40)
		binary.BigEndian.PutUint16(request[0:2], usbip.Version)
		binary.BigEndian.PutUint16(request[2:4], usbip.OpReqImport)
		copy(request[8:], "379-1")
		if _, err := client.Write(request); err != nil {
			t.Fatal(err)
		}
		header := make([]byte, 8)
		if _, err := io.ReadFull(client, header); err != nil {
			t.Fatal(err)
		}
		status := binary.BigEndian.Uint32(header[4:8])
		if status != 0 {
			t.Logf("import=%d rejected status=%d", i, status)
			continue
		}
		descriptor := make([]byte, 312)
		if _, err := io.ReadFull(client, descriptor); err != nil {
			t.Fatal(err)
		}
		_ = client.SetDeadline(time.Time{})
		accepted++
		t.Logf("import=%d status=success same_export=379-1 prior_connections_kept_open=%d", i, i-1)
	}
	t.Log(fmt.Sprintf("AUDIT_RESULT active_connections_same_virtual_device=%d expected_exclusive_limit=1 driver_used=false", accepted))
	if accepted != 1 {
		t.Fatalf("duplicate imports accepted=%d, want exactly 1", accepted)
	}
}

func TestFourImportLeasesAndCancelledGeneration(t *testing.T) {
	s := New(ServerConfig{}, slog.New(slog.NewTextHandler(io.Discard, nil)), nil)
	type lease struct {
		release func()
		cancel  context.CancelFunc
		peer    net.Conn
	}
	all := make([]lease, 4)
	for i := range all {
		ctx, cancel := context.WithCancel(context.Background())
		a, b := net.Pipe()
		defer a.Close()
		defer b.Close()
		defer cancel()
		release, ok := s.claimImport(fmt.Sprintf("%d-1", 100+i), ctx, a)
		if !ok {
			t.Fatalf("slot %d refused", i)
		}
		all[i] = lease{release, cancel, b}
		defer release()
	}
	// Removing one export closes its host socket, and permits a new generation.
	all[1].cancel()
	_ = all[1].peer.SetReadDeadline(time.Now().Add(time.Second))
	if _, err := all[1].peer.Read(make([]byte, 1)); err == nil {
		t.Fatal("removed export kept import socket alive")
	}
	next, cancel := context.WithCancel(context.Background())
	defer cancel()
	a, b := net.Pipe()
	defer a.Close()
	defer b.Close()
	release, ok := s.claimImport("101-1", next, a)
	if !ok {
		t.Fatal("cancelled generation blocks replacement")
	}
	defer release()
	all[1].release() // Late old cleanup must not release the new owner.
	if r, ok := s.claimImport("101-1", next, b); ok {
		r()
		t.Fatal("old cleanup released the new owner")
	}
	for _, i := range []int{0, 2, 3} {
		if r, ok := s.claimImport(fmt.Sprintf("%d-1", 100+i), next, b); ok {
			r()
			t.Fatalf("sibling %d lease disappeared", i)
		}
	}
	release()
	if r, ok := s.claimImport("101-1", next, a); !ok {
		t.Fatal("clean release blocks reattach")
	} else {
		r()
	}
}

func TestOldCleanupCannotRemoveReusedDeviceID(t *testing.T) {
	bus := virtualbus.New(497)
	pad := &auditImportPad{}
	old, err := bus.Add(pad)
	if err != nil {
		t.Fatal(err)
	}
	if err := bus.RemoveDeviceByID("1"); err != nil {
		t.Fatal(err)
	}
	current, err := bus.Add(&auditImportPad{})
	if err != nil {
		t.Fatal(err)
	}
	if err := bus.RemoveDeviceIfContext("1", old); err == nil {
		t.Fatal("old cleanup removed reused ID")
	}
	if current.Err() != nil || len(bus.Devices()) != 1 {
		t.Fatal("replacement was cancelled")
	}
	if err := bus.RemoveDeviceIfContext("1", current); err != nil {
		t.Fatal(err)
	}
}
