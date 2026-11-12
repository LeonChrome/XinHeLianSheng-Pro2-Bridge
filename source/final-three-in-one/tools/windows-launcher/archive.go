package main

import (
	"archive/zip"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
)

type payloadFile struct {
	Name   string `json:"name"`
	Size   int64  `json:"size"`
	SHA256 string `json:"sha256"`
}

func relativeFile(root, name string) (string, error) {
	name = strings.ReplaceAll(name, "\\", "/")
	if name == "" || strings.Contains(name, ":") || strings.HasPrefix(name, "/") {
		return "", fmt.Errorf("invalid payload path")
	}
	for _, segment := range strings.Split(name, "/") {
		if segment == ".." || segment == "." || strings.HasSuffix(segment, ".") || strings.HasSuffix(segment, " ") {
			return "", fmt.Errorf("invalid payload segment")
		}
	}
	clean := filepath.FromSlash(name)
	full := filepath.Join(root, clean)
	rel, err := filepath.Rel(root, full)
	if err != nil || rel == ".." || strings.HasPrefix(rel, ".."+string(filepath.Separator)) {
		return "", fmt.Errorf("payload outside runtime")
	}
	return full, nil
}

func verifyFiles(root string, files []payloadFile) error {
	for _, file := range files {
		path, err := relativeFile(root, file.Name)
		if err != nil {
			return err
		}
		info, err := os.Lstat(path)
		if err != nil {
			return err
		}
		if !info.Mode().IsRegular() || info.Size() != file.Size {
			return fmt.Errorf("runtime file changed: %s", file.Name)
		}
		stream, err := os.Open(path)
		if err != nil {
			return err
		}
		hash := sha256.New()
		_, err = io.Copy(hash, stream)
		stream.Close()
		if err != nil {
			return err
		}
		if !strings.EqualFold(hex.EncodeToString(hash.Sum(nil)), file.SHA256) {
			return fmt.Errorf("runtime checksum failed: %s", file.Name)
		}
	}
	return nil
}

func extractFiles(reader *zip.Reader, root string, files []payloadFile) error {
	expected := map[string]payloadFile{}
	for _, f := range files {
		expected[strings.ToLower(f.Name)] = f
	}
	var total uint64
	for _, z := range reader.File {
		if z.FileInfo().IsDir() {
			continue
		}
		key := strings.ToLower(z.Name)
		item, ok := expected[key]
		if !ok || !z.Mode().IsRegular() || item.Size < 0 || uint64(item.Size) != z.UncompressedSize64 {
			return fmt.Errorf("unexpected payload entry: %s", z.Name)
		}
		total += z.UncompressedSize64
		if total > 600*1024*1024 {
			return fmt.Errorf("payload exceeds size limit")
		}
		path, err := relativeFile(root, z.Name)
		if err != nil {
			return err
		}
		if err = os.MkdirAll(filepath.Dir(path), 0700); err != nil {
			return err
		}
		in, err := z.Open()
		if err != nil {
			return err
		}
		out, err := os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
		if err != nil {
			in.Close()
			return err
		}
		_, copyErr := io.Copy(out, io.LimitReader(in, item.Size+1))
		closeErr := out.Close()
		in.Close()
		if copyErr != nil {
			return copyErr
		}
		if closeErr != nil {
			return closeErr
		}
		delete(expected, key)
	}
	if len(expected) > 0 {
		return fmt.Errorf("incomplete payload")
	}
	return verifyFiles(root, files)
}

func shouldRetrySafe(code uint32, elapsedSeconds float64, safe bool) bool {
	return !safe && elapsedSeconds < 20 && (code == 0xc0000005 || code == 0xc0000409 || code == 0xe0434352)
}
