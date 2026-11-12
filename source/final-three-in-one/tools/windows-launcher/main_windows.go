package main

import (
	"archive/zip"
	"bytes"
	"crypto/sha256"
	_ "embed"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"runtime/debug"
	"strings"
	"syscall"
	"time"
	"unsafe"
)

//go:embed payload.zip
var payload []byte

//go:embed payload-manifest.json
var fileManifest []byte

var user32 = syscall.NewLazyDLL("user32.dll")
var messageBox = user32.NewProc("MessageBoxW")
var kernel32 = syscall.NewLazyDLL("kernel32.dll")
var logPath string

func showError(message string) {
	caption, _ := syscall.UTF16PtrFromString("PRO2 手柄助手")
	body, _ := syscall.UTF16PtrFromString(message + "\n\n启动记录：" + logPath)
	messageBox.Call(0, uintptr(unsafe.Pointer(body)), uintptr(unsafe.Pointer(caption)), 0x10)
}

func main() {
	// Restrict OS critical-error dialogs to this process tree; no global setting.
	kernel32.NewProc("SetErrorMode").Call(0x0001 | 0x0002 | 0x8000)
	root := os.Getenv("LOCALAPPDATA")
	if root == "" {
		root = os.TempDir()
	}
	logs := filepath.Join(root, "PRO2WirelessReceiverControlBoard", "v6_logs")
	if err := os.MkdirAll(logs, 0700); err != nil {
		logs = os.TempDir()
	}
	logPath = filepath.Join(logs, fmt.Sprintf("launcher_%s_%d.log", time.Now().Format("20060102_150405"), os.Getpid()))
	output, err := os.OpenFile(logPath, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0600)
	if err == nil {
		defer output.Close()
		log.SetOutput(output)
	}
	defer func() {
		if failure := recover(); failure != nil {
			log.Printf("panic=%v", failure)
			showError("启动程序遇到错误。请保留本记录。")
			os.Exit(1)
		}
	}()
	log.Printf("native_entry launcher_version=2.9.0 system_writes=false")
	var files []payloadFile
	if err = json.Unmarshal(fileManifest, &files); err != nil {
		showError("内置运行文件清单无效。")
		os.Exit(1)
	}
	digest := sha256.Sum256(payload)
	stamp := hex.EncodeToString(digest[:8])
	cacheBase := filepath.Join(root, "PRO2WirelessReceiverControlBoard", "app", "2.9.0-"+stamp)
	runtime, err := prepareRuntime(cacheBase, files)
	if err != nil {
		log.Printf("prepare_error=%v", err)
		showError("无法准备程序文件：" + err.Error() + "\n请检查剩余磁盘空间、目录权限和安全软件的拦截记录。")
		os.Exit(1)
	}
	launcher, _ := os.Executable()
	args := append([]string{}, os.Args[1:]...)
	args = append(args, "--software-rendering")
	safe := os.Getenv("PRO2_STARTUP_SAFE") == "1"
	code, elapsed, err := runChild(runtime, launcher, args, safe)
	if err != nil {
		log.Printf("child_start_error=%v", err)
		showError("无法打开主程序：" + err.Error())
		os.Exit(1)
	}
	if shouldRetrySafe(code, elapsed, safe) && len(os.Args) == 1 {
		log.Printf("early_native_failure code=0x%08X retry=safe_startup once_only=true", code)
		code, _, err = runChild(runtime, launcher, args, true)
	}
	if err != nil || code >= 0x80000000 {
		showError(fmt.Sprintf("程序未能正常运行（0x%08X）。\n已使用独立运行目录和软件绘制；本机仍发生原生程序错误，请保留启动记录。", code))
	}
	if code != 0 {
		os.Exit(int(code))
	}
}

func prepareRuntime(base string, files []payloadFile) (string, error) {
	if len(files) == 0 || len(files) > 3000 {
		return "", fmt.Errorf("invalid file manifest")
	}
	// Serialize extraction for double-clicks; never delete or overwrite a running
	// application's runtime directory. Corrupt caches get a separate repair copy.
	hash := sha256.Sum256([]byte(strings.ToLower(base)))
	name, _ := syscall.UTF16PtrFromString(fmt.Sprintf("Local\\PRO2Runtime_%x", hash[:12]))
	handle, _, callErr := kernel32.NewProc("CreateMutexW").Call(0, 0, uintptr(unsafe.Pointer(name)))
	if handle == 0 {
		return "", callErr
	}
	defer syscall.CloseHandle(syscall.Handle(handle))
	result, _, _ := kernel32.NewProc("WaitForSingleObject").Call(handle, 60000)
	if result != 0 && result != 0x80 {
		return "", fmt.Errorf("another startup is preparing files; retry shortly")
	}
	defer kernel32.NewProc("ReleaseMutex").Call(handle)
	active := base
	if pointer, err := os.ReadFile(base + ".active"); err == nil {
		// Only accept sibling repair directories made for this exact payload.
		value := string(pointer)
		if strings.HasPrefix(value, base+"-repair-") && filepath.Dir(value) == filepath.Dir(base) {
			active = value
		}
	}
	if verifyFiles(active, files) == nil {
		log.Printf("runtime_verified cache=%q", active)
		return active, nil
	}
	if _, err := os.Stat(active); err == nil {
		active = base + "-repair-" + time.Now().Format("20060102150405.000000000")
	}
	stage := active + fmt.Sprintf(".preparing-%d", os.Getpid())
	if err := os.MkdirAll(filepath.Dir(stage), 0700); err != nil {
		return "", err
	}
	if err := os.Mkdir(stage, 0700); err != nil {
		return "", err
	}
	// This newly created temporary directory belongs only to this process.
	defer func() {
		parent, _ := filepath.Abs(filepath.Dir(base))
		target, _ := filepath.Abs(stage)
		rel, err := filepath.Rel(parent, target)
		if err == nil && rel != "." && rel != ".." && !strings.HasPrefix(rel, ".."+string(filepath.Separator)) && strings.HasSuffix(target, fmt.Sprintf(".preparing-%d", os.Getpid())) {
			_ = os.RemoveAll(target)
		}
	}()
	reader, err := zip.NewReader(bytes.NewReader(payload), int64(len(payload)))
	if err != nil {
		return "", err
	}
	log.Printf("extract_begin files=%d directory=%q", len(files), stage)
	if err = extractFiles(reader, stage, files); err != nil {
		return "", err
	}
	if err = os.Rename(stage, active); err != nil {
		return "", err
	}
	if active != base {
		tempPointer := base + fmt.Sprintf(".active-%d.tmp", os.Getpid())
		if err = os.WriteFile(tempPointer, []byte(active), 0600); err == nil {
			_ = os.Rename(tempPointer, base+".active")
		}
	}
	log.Printf("extract_verified runtime=%q", active)
	return active, nil
}

func runChild(runtime, launcher string, args []string, safe bool) (uint32, float64, error) {
	child := exec.Command(filepath.Join(runtime, "PRO2ControllerAssistant.exe"), args...)
	child.Dir = filepath.Dir(launcher)
	child.Env = append(os.Environ(), "PRO2_LAUNCHER_PATH="+launcher, "PRO2_STARTUP_SAFE="+map[bool]string{true: "1", false: "0"}[safe])
	if safe {
		child.Env = append(child.Env, "DOTNET_ReadyToRun=0", "DOTNET_EnableHWIntrinsic=0")
	}
	child.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	child.Stdout = log.Writer()
	child.Stderr = log.Writer()
	start := time.Now()
	if err := child.Start(); err != nil {
		return 0, 0, err
	}
	log.Printf("child_started pid=%d safe=%t executable=%q", child.Process.Pid, safe, child.Path)
	// The launcher now only waits for its child. Release extraction heap and
	// trim this process's own working set, including read-only embedded ZIP
	// pages. No working-set or power policy is applied to the controller app.
	debug.FreeOSMemory()
	self, _, _ := kernel32.NewProc("GetCurrentProcess").Call()
	kernel32.NewProc("SetProcessWorkingSetSize").Call(self, ^uintptr(0), ^uintptr(0))
	err := child.Wait()
	elapsed := time.Since(start).Seconds()
	code := uint32(child.ProcessState.ExitCode())
	log.Printf("child_exit code=0x%08X elapsed_s=%.3f safe=%t", code, elapsed, safe)
	if _, ok := err.(*exec.ExitError); err != nil && !ok {
		return code, elapsed, err
	}
	return code, elapsed, nil
}
