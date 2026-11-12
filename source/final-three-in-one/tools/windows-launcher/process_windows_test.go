package main

import (
	"io"
	"os"
	"path/filepath"
	"testing"
)

func TestMain(m *testing.M) {
	if os.Getenv("PRO2_LAUNCHER_TEST_CHILD") == "1" {
		if os.Getenv("PRO2_STARTUP_SAFE") == "1" {
			os.Exit(0)
		}
		// A voluntary exit with the native AV status exercises the monitor without
		// causing a real fault, WER dialog, or touching a real assistant process.
		os.Exit(int(uint32(0xc0000005)))
	}
	os.Exit(m.Run())
}

func TestNativeExitReachesSafeChild(t *testing.T) {
	t.Setenv("PRO2_LAUNCHER_TEST_CHILD", "1")
	root := t.TempDir()
	self, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	in, err := os.Open(self)
	if err != nil {
		t.Fatal(err)
	}
	defer in.Close()
	out, err := os.Create(filepath.Join(root, "PRO2ControllerAssistant.exe"))
	if err != nil {
		t.Fatal(err)
	}
	_, err = io.Copy(out, in)
	out.Close()
	if err != nil {
		t.Fatal(err)
	}
	first, elapsed, err := runChild(root, filepath.Join(root, "launcher.exe"), nil, false)
	if err != nil || !shouldRetrySafe(first, elapsed, false) {
		t.Fatalf("native exit was not retryable: %08x %v", first, err)
	}
	second, _, err := runChild(root, filepath.Join(root, "launcher.exe"), nil, true)
	if err != nil || second != 0 {
		t.Fatalf("safe child failed: %08x %v", second, err)
	}
}
