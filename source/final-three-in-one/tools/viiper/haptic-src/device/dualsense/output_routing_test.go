package dualsense

import (
	"bytes"
	"context"
	"encoding/binary"
	"io"
	"log/slog"
	"net"
	"os"
	"path/filepath"
	"strconv"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/Alia5/VIIPER/usbip"
)

func rumbleRoutingReport(size int, weak, strong byte) []byte {
	report := make([]byte, size)
	report[0], report[1], report[3], report[4] = ReportIDOutput, 3, weak, strong
	return report
}

func sendRoutingReport(d *DualSense, control bool, report []byte) bool {
	if control {
		_, handled := d.HandleControl(0x21, 0x09, 0x0202, 3, uint16(len(report)), report)
		return handled
	}
	d.HandleTransfer(context.Background(), 3, usbip.DirOut, report)
	return true
}

func TestHIDOutputRoutesOrdinaryStartAndStopExactlyOnce(t *testing.T) {
	for _, control := range []bool{false, true} {
		for _, size := range []int{48, 64} {
			route := "interrupt"
			if control {
				route = "control"
			}
			t.Run(route+"/size_"+strconv.Itoa(size), func(t *testing.T) {
				d := &DualSense{hapticEnabled: true}
				var rawReports [][]byte
				var legacy []OutputState
				d.SetRawOutputCallback(func(report []byte) { rawReports = append(rawReports, report) })
				d.SetOutputCallback(func(state OutputState) { legacy = append(legacy, state) })
				for _, report := range [][]byte{rumbleRoutingReport(size, 100, 150), rumbleRoutingReport(size, 0, 0)} {
					if !sendRoutingReport(d, control, report) {
						t.Fatal("valid output rejected")
					}
				}
				if len(rawReports) != 2 || len(legacy) != 2 {
					t.Fatalf("duplicate or missing start/stop: raw=%d legacy=%d", len(rawReports), len(legacy))
				}
				if rawReports[0][3] != 100 || rawReports[0][4] != 150 || rawReports[1][3] != 0 || rawReports[1][4] != 0 {
					t.Fatal("ordinary motor data or explicit stop altered")
				}
				if legacy[0].RumbleSmall != 100 || legacy[0].RumbleLarge != 150 || legacy[1].RumbleSmall != 0 || legacy[1].RumbleLarge != 0 {
					t.Fatal("legacy feedback regression")
				}
			})
		}
	}
}

func TestHIDOutputDoesNotConvertInvalidOrUnrelatedRequests(t *testing.T) {
	for _, control := range []bool{false, true} {
		for _, report := range [][]byte{nil, {2}, make([]byte, 47), make([]byte, 64), append([]byte{0x31}, make([]byte, 63)...)} {
			d := &DualSense{hapticEnabled: true}
			callbacks := 0
			d.SetRawOutputCallback(func([]byte) { callbacks++ })
			d.SetOutputCallback(func(OutputState) { callbacks++ })
			sendRoutingReport(d, control, report)
			if callbacks != 0 {
				t.Fatal("invalid report delivered as vibration")
			}
		}
	}
	d := &DualSense{hapticEnabled: true}
	callbacks := 0
	d.SetRawOutputCallback(func([]byte) { callbacks++ })
	report := rumbleRoutingReport(64, 255, 255)
	for _, value := range []uint16{0x0302, 0x0231} {
		d.HandleControl(0x21, 0x09, value, 3, 64, report)
	}
	d.HandleTransfer(context.Background(), 2, usbip.DirOut, report)
	if callbacks != 0 {
		t.Fatal("feature, other ID, or unrelated endpoint became rumble")
	}
	if !sendRoutingReport(&DualSense{hapticEnabled: true}, true, report) {
		t.Fatal("valid request without a registered consumer should be handled safely")
	}
}

func TestHIDOutputCopiesReportsAndInvokesOutsideMutex(t *testing.T) {
	for _, control := range []bool{false, true} {
		d := &DualSense{hapticEnabled: true}
		report := rumbleRoutingReport(64, 100, 150)
		finished := make(chan struct{})
		d.SetRawOutputCallback(func(received []byte) {
			received[3] = 0
			d.SetRawOutputCallback(nil)
			d.SetOutputCallback(nil)
		})
		go func() { sendRoutingReport(d, control, report); close(finished) }()
		select {
		case <-finished:
		case <-time.After(2 * time.Second):
			t.Fatal("callback invoked while holding device mutex")
		}
		if report[3] != 100 {
			t.Fatal("callback mutated caller-owned USB data")
		}
	}
}

func TestHIDOutputConcurrentRoutesKeepExactlyOneRawCallbackPerRequest(t *testing.T) {
	d := &DualSense{hapticEnabled: true}
	var received atomic.Uint64
	d.SetRawOutputCallback(func([]byte) { received.Add(1) })
	var group sync.WaitGroup
	for i := 0; i < 64; i++ {
		group.Add(1)
		go func(control bool) {
			defer group.Done()
			sendRoutingReport(d, control, rumbleRoutingReport(64, 100, 150))
		}(i%2 == 0)
	}
	group.Wait()
	if received.Load() != 64 || d.controlOutputFrames != 32 || d.interruptOutputFrames != 32 {
		t.Fatalf("lost/duplicate output: raw=%d control=%d interrupt=%d", received.Load(), d.controlOutputFrames, d.interruptOutputFrames)
	}
}

func TestHapticStreamForwardsBothHIDRoutesAndKeepsPCM(t *testing.T) {
	handler := &hapticHandler{}
	dev, err := handler.CreateDevice(nil)
	if err != nil {
		t.Fatal(err)
	}
	d := dev.(*DualSense)
	server, client := net.Pipe()
	finished := make(chan error, 1)
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	go func() { finished <- handler.StreamHandler()(server, &dev, logger) }()
	t.Cleanup(func() {
		client.Close()
		server.Close()
		select {
		case <-finished:
		case <-time.After(2 * time.Second):
			t.Error("feedback stream failed to exit")
		}
	})
	deadline := time.Now().Add(2 * time.Second)
	for {
		d.mtx.Lock()
		ready := d.rawOutputFunc != nil && d.hapticAudioFunc != nil
		d.mtx.Unlock()
		if ready {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("stream callbacks not registered")
		}
		time.Sleep(time.Millisecond)
	}
	if err := client.SetDeadline(time.Now().Add(5 * time.Second)); err != nil {
		t.Fatal(err)
	}
	native := rumbleRoutingReport(64, 77, 88)
	native[1] = 0
	cases := []struct {
		name    string
		kind    byte
		control bool
		payload []byte
	}{
		{"control-start", 1, true, rumbleRoutingReport(64, 100, 150)},
		{"interrupt-update", 1, false, rumbleRoutingReport(64, 25, 90)},
		{"control-stop", 1, true, rumbleRoutingReport(64, 0, 0)},
		{"native-mode", 1, true, native},
		{"audio-pcm", 2, false, make([]byte, HapticAudioPacketSize)},
	}
	for _, item := range cases {
		sent := make(chan bool, 1)
		go func() {
			if item.kind == 2 {
				d.HandleTransfer(context.Background(), 1, usbip.DirOut, item.payload)
				sent <- true
			} else {
				sent <- sendRoutingReport(d, item.control, item.payload)
			}
		}()
		wire := make([]byte, HapticFeedbackFrameSize)
		if _, err := io.ReadFull(client, wire); err != nil {
			t.Fatalf("%s: %v", item.name, err)
		}
		if wire[0] != item.kind || int(binary.LittleEndian.Uint16(wire[1:3])) != len(item.payload) ||
			!bytes.Equal(wire[4:4+len(item.payload)], item.payload) {
			t.Fatalf("%s: wire frame corrupted", item.name)
		}
		if !<-sent {
			t.Fatalf("%s: send rejected", item.name)
		}
		if directory := os.Getenv("PRO2_FEEDBACK_FIXTURE_DIR"); directory != "" {
			if err := os.MkdirAll(directory, 0755); err != nil {
				t.Fatal(err)
			}
			if err := os.WriteFile(filepath.Join(directory, item.name+".bin"), wire, 0644); err != nil {
				t.Fatal(err)
			}
		}
		t.Logf("%s: kind=%d payload_bytes=%d wire_bytes=%d", item.name, wire[0], len(item.payload), len(wire))
	}
	if d.controlOutputFrames != 3 || d.interruptOutputFrames != 1 || d.hapticAudioFrames != 1 {
		t.Fatal("unexpected stream input route counters")
	}
}
