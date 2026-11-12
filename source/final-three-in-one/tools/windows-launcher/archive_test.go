package main

import (
	"archive/zip"
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"os"
	"path/filepath"
	"testing"
)

func TestArchiveIntegrityAndNoTraversal(t *testing.T) {
	root := t.TempDir()
	for _, name := range []string{"../outside.exe", "C:/outside.exe", "/outside.exe", "sub/../../outside.exe", "sub:stream", "sub/../file", "sub./file"} {
		if _, err := relativeFile(root, name); err == nil {
			t.Fatalf("accepted unsafe path %q", name)
		}
	}
	data := []byte("packaged runtime fixture")
	sha := sha256.Sum256(data)
	manifest := []payloadFile{{Name: "sub/fixture.dll", Size: int64(len(data)), SHA256: hex.EncodeToString(sha[:])}}
	var buffer bytes.Buffer
	writer := zip.NewWriter(&buffer)
	entry, _ := writer.Create("sub/fixture.dll")
	entry.Write(data)
	writer.Close()
	reader, err := zip.NewReader(bytes.NewReader(buffer.Bytes()), int64(buffer.Len()))
	if err != nil {
		t.Fatal(err)
	}
	if err = extractFiles(reader, root, manifest); err != nil {
		t.Fatal(err)
	}
	if err = verifyFiles(root, manifest); err != nil {
		t.Fatal(err)
	}
	if err = os.WriteFile(filepath.Join(root, "sub", "fixture.dll"), []byte("changed"), 0600); err != nil {
		t.Fatal(err)
	}
	if err = verifyFiles(root, manifest); err == nil {
		t.Fatal("modified runtime accepted")
	}
	var bad bytes.Buffer
	writer = zip.NewWriter(&bad)
	entry, _ = writer.Create("../outside.exe")
	entry.Write(data)
	writer.Close()
	reader, _ = zip.NewReader(bytes.NewReader(bad.Bytes()), int64(bad.Len()))
	if extractFiles(reader, t.TempDir(), []payloadFile{{Name: "../outside.exe", Size: int64(len(data)), SHA256: hex.EncodeToString(sha[:])}}) == nil {
		t.Fatal("archive traversal accepted")
	}
}

func TestSafeFallbackBounded(t *testing.T) {
	if !shouldRetrySafe(0xc0000005, 2, false) {
		t.Fatal("early native fault did not retry")
	}
	for _, trial := range []struct {
		code    uint32
		elapsed float64
		safe    bool
	}{
		{0, 2, false}, {1, 2, false}, {2, 2, false}, {6, 2, false}, {0xc0000005, 60, false}, {0xc0000005, 2, true},
	} {
		if shouldRetrySafe(trial.code, trial.elapsed, trial.safe) {
			t.Fatalf("unexpected retry %+v", trial)
		}
	}
}
