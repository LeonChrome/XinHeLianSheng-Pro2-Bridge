// Package ns2pro provides a Nintendo Switch 2 Pro Controller compatible HID device.
package ns2pro

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"sync"
	"time"

	"github.com/Alia5/VIIPER/device"
	"github.com/Alia5/VIIPER/usb"
	"github.com/Alia5/VIIPER/usbip"
)

type NS2Pro struct {
	inputCh                 chan struct{}
	bulkCh                  chan struct{}
	stateMu                 sync.Mutex
	inputState              *InputState
	inputQueue              *device.InputQueue[InputState]
	metaState               *MetaState
	outputMu                sync.RWMutex
	outputCallback          func(OutputState)
	outputVersion           uint64
	descriptor              usb.Descriptor
	inputIntervalMs         uint8
	sourcePaced             bool
	protocolClockResample   bool
	clockBase               time.Time
	lastSourceArrival       time.Time
	staleStage              uint8
	motionClockInitialized  bool
	lastWallClockUS         uint64
	lastPhysicalSourceUS    uint32
	havePhysicalSourceClock bool
	sourceClockUS           uint64

	protoMu           sync.Mutex
	activeReportID    uint8
	featureMask       uint8
	featureFlags      uint8
	usbReportsEnabled bool
	reportCounter32   uint32
	reportCounter8    uint8
	lastMotionTS      uint32
	bulkInQueue       [][]byte
}

func New(o *device.CreateOptions) (*NS2Pro, error) {
	metaState := defaultMetaState()
	if o != nil && o.DeviceSpecific != "" {
		var newMeta MetaState
		if err := json.Unmarshal([]byte(o.DeviceSpecific), &newMeta); err != nil {
			return nil, fmt.Errorf("invalid device specific JSON: %w", err)
		}
		if newMeta.SerialNumber != "" {
			metaState.SerialNumber = newMeta.SerialNumber
		}
		if newMeta.BatteryLevel != 0 {
			metaState.BatteryLevel = newMeta.BatteryLevel
		}
		if newMeta.Charging {
			metaState.Charging = true
		}
		if newMeta.ExternalPower {
			metaState.ExternalPower = true
		}
		if newMeta.BatteryVolts != 0 {
			metaState.BatteryVolts = newMeta.BatteryVolts
		}
		if newMeta.InputIntervalMs != 0 {
			metaState.InputIntervalMs = newMeta.InputIntervalMs
		}
		metaState.SourcePaced = newMeta.SourcePaced
		metaState.ProtocolClockResample = newMeta.ProtocolClockResample
	}

	inputCh := make(chan struct{}, 1)
	if metaState.ProtocolClockResample {
		metaState.InputIntervalMs = 4
	}
	inputCh <- struct{}{}
	initialInput := defaultInputState()
	d := &NS2Pro{
		inputCh:               inputCh,
		bulkCh:                make(chan struct{}, 1),
		inputState:            initialInput,
		metaState:             metaState,
		descriptor:            MakeDescriptor(),
		inputIntervalMs:       metaState.InputIntervalMs,
		sourcePaced:           metaState.SourcePaced,
		protocolClockResample: metaState.ProtocolClockResample,
		clockBase:             time.Now(),
		lastSourceArrival:     time.Now(),
		activeReportID:        ReportIDPro,
		featureFlags:          FeatureButtons | FeatureSticks,
	}
	d.inputQueue = device.NewInputQueue[InputState](64, func(a, b InputState) bool { return a.Buttons == b.Buttons })
	d.inputQueue.Seed(*initialInput)
	d.inputQueue.SetTelemetry("pro2", d.InputClockPolicy())
	serialEnding := DefaultSerialEnding
	if len(metaState.SerialNumber) >= 2 {
		serialEnding = metaState.SerialNumber[len(metaState.SerialNumber)-2:]
	}
	d.descriptor.Strings[3] = serialEnding
	if len(d.descriptor.Interfaces) > 0 && len(d.descriptor.Interfaces[0].Endpoints) > 0 {
		d.descriptor.Interfaces[0].Endpoints[0].BInterval = metaState.InputIntervalMs
	}

	if o != nil {
		if o.IDVendor != nil {
			d.descriptor.Device.IDVendor = *o.IDVendor
		}
		if o.IDProduct != nil {
			d.descriptor.Device.IDProduct = *o.IDProduct
		}
	}
	return d, nil
}

func (d *NS2Pro) SetOutputCallback(f func(OutputState)) func() {
	d.outputMu.Lock()
	d.outputVersion++
	version := d.outputVersion
	d.outputCallback = f
	d.outputMu.Unlock()

	return func() {
		d.outputMu.Lock()
		if d.outputVersion == version {
			d.outputCallback = nil
		}
		d.outputMu.Unlock()
	}
}

func (d *NS2Pro) UpdateInputState(state InputState) {
	d.stateMu.Lock()
	wasStale := d.staleStage
	if wasStale != 0 {
		d.inputQueue.DiscardStale()
		d.staleStage = 0
	}
	d.inputState = &state
	d.lastSourceArrival = time.Now()
	d.inputQueue.Push(state)
	d.stateMu.Unlock()
	if wasStale != 0 {
		slog.Info("[VIIPER_INPUT_SAFETY]", "mode", "pro2", "event", "fresh_source_resumed", "old_stage", wasStale)
	}
	d.signalInputReady()
}
func (d *NS2Pro) SourcePacedInput() bool                     { return d.sourcePaced && !d.protocolClockResample }
func (d *NS2Pro) InputTransferStats() device.InputQueueStats { return d.inputQueue.Stats() }
func (d *NS2Pro) RecordInputRepeat()                         { d.inputQueue.RecordRepeat() }
func (d *NS2Pro) InputClockPolicy() string {
	if d.protocolClockResample {
		return "protocol_4ms_wall_zero_order_hold"
	}
	if d.sourcePaced {
		return "physical_source_us"
	}
	return "legacy_endpoint_cadence"
}

func (d *NS2Pro) SetMetaState(meta MetaState) {
	d.stateMu.Lock()
	defer d.stateMu.Unlock()
	d.metaState = &meta
	if d.descriptor.Strings != nil {
		serialEnding := DefaultSerialEnding
		if len(meta.SerialNumber) >= 2 {
			serialEnding = meta.SerialNumber[len(meta.SerialNumber)-2:]
		}
		d.descriptor.Strings[3] = serialEnding
	}
}

func (d *NS2Pro) HandleTransfer(ctx context.Context, ep uint32, dir uint32, out []byte) []byte {
	switch {
	case dir == usbip.DirIn && ep == 1:
		if d.reportsEnabled() {
			if d.sourcePaced && !d.protocolClockResample {
				for d.inputQueue.Stats().Depth == 0 {
					select {
					case <-ctx.Done():
						return nil
					case <-d.inputCh:
					}
				}
			}
			return d.nextInputReport()
		}
		for {
			select {
			case <-ctx.Done():
				if errors.Is(ctx.Err(), context.DeadlineExceeded) && d.reportsEnabled() && (!d.sourcePaced || d.protocolClockResample) {
					return d.nextInputReport()
				}
				return nil
			case <-d.inputCh:
				if d.reportsEnabled() {
					return d.nextInputReport()
				}
			}
		}
	case dir == usbip.DirIn && ep == 2:
		for {
			if resp := d.popBulkIn(); resp != nil {
				return resp
			}
			select {
			case <-ctx.Done():
				return nil
			case <-d.bulkCh:
			}
		}
	case dir == usbip.DirOut && ep == 1:
		d.handleOutputReport(out)
	case dir == usbip.DirOut && ep == 2:
		d.handleBulkOut(out)
	}
	return nil
}

func (d *NS2Pro) HandleControl(bmRequestType, bRequest uint8, wValue, wIndex uint16, wLength uint16, data []byte) ([]byte, bool) {
	reportType := uint8(wValue >> 8)
	reportID := uint8(wValue)

	if bmRequestType == hidClassRequestIn && bRequest == hidGetReport && reportType == reportTypeInput {
		switch reportID {
		case ReportIDCommon, ReportIDPro, 0:
			return d.inputReportForID(reportID), true
		}
	}

	if bmRequestType == hidClassRequestOut && bRequest == hidSetReport && reportType == reportTypeOutput && reportID == ReportIDOutput {
		d.handleOutputReport(data)
		return nil, true
	}

	if isAudioClassRequest(bmRequestType) {
		switch bRequest {
		case audioSetCur:
			return nil, true
		case audioGetCur, audioGetMin, audioGetMax, audioGetRes:
			return make([]byte, wLength), true
		}
	}

	return nil, false
}

func isAudioClassRequest(bmRequestType uint8) bool {
	return bmRequestType&requestTypeMask == requestClass &&
		(bmRequestType&recipientMask == recipientIface || bmRequestType&recipientMask == recipientEndpoint)
}

func (d *NS2Pro) GetDescriptor() *usb.Descriptor {
	return &d.descriptor
}

func (d *NS2Pro) GetDeviceSpecificArgs() map[string]any {
	d.stateMu.Lock()
	defer d.stateMu.Unlock()
	if d.metaState == nil {
		return map[string]any{}
	}
	b, err := json.Marshal(d.metaState)
	if err != nil {
		return map[string]any{}
	}
	var out map[string]any
	if err := json.Unmarshal(b, &out); err != nil {
		return map[string]any{}
	}
	out["input_transfer"] = d.inputQueue.Stats()
	// Battery metadata updates must not misreport immutable creation policy.
	out["source_paced"] = d.sourcePaced
	out["protocol_clock_resample"] = d.protocolClockResample
	out["input_interval_ms"] = d.inputIntervalMs
	out["input_clock_policy"] = d.InputClockPolicy()
	return out
}

func (d *NS2Pro) reportsEnabled() bool {
	d.protoMu.Lock()
	defer d.protoMu.Unlock()
	return d.usbReportsEnabled
}

func (d *NS2Pro) nextInputReport() []byte {
	d.protoMu.Lock()
	reportID := d.activeReportID
	d.protoMu.Unlock()
	if d.protocolClockResample {
		d.stateMu.Lock()
		age := time.Since(d.lastSourceArrival)
		st, stage := resampleSafetyState(*d.inputState, age)
		oldStage := d.staleStage
		d.staleStage = stage
		if stage == 2 {
			d.inputQueue.DiscardStale()
		}
		d.stateMu.Unlock()
		if stage != oldStage {
			slog.Info("[VIIPER_INPUT_SAFETY]", "mode", "pro2", "event", "source_stale_guard", "stage", stage, "source_age_ms", age.Milliseconds(), "fresh_source", false)
		}
		if stage != 0 {
			d.inputQueue.RecordRepeat()
			return d.inputReportForState(reportID, &st)
		}
	}
	if st, ok := d.inputQueue.Pop(); ok {
		return d.inputReportForState(reportID, &st)
	}
	d.inputQueue.RecordRepeat()
	return d.inputReportForID(reportID)
}

// Safety synthesis is not a new physical sample. Keep gravity, stop integrating
// stale angular velocity after 100ms; release controls only after a 500ms gap.
func resampleSafetyState(st InputState, age time.Duration) (InputState, uint8) {
	if age < 100*time.Millisecond {
		return st, 0
	}
	st.GyroX, st.GyroY, st.GyroZ = 0, 0, 0
	if age < 500*time.Millisecond {
		return st, 1
	}
	st.Buttons = 0
	st.LX, st.LY, st.RX, st.RY = StickCenter, StickCenter, StickCenter, StickCenter
	return st, 2
}

func (d *NS2Pro) inputReportForID(reportID uint8) []byte {
	return d.inputReportForState(reportID, nil)
}
func (d *NS2Pro) inputReportForState(reportID uint8, selected *InputState) []byte {
	d.stateMu.Lock()
	st := *d.inputState
	if selected != nil {
		st = *selected
	}
	meta := *d.metaState
	d.stateMu.Unlock()

	d.protoMu.Lock()
	if reportID == 0 {
		reportID = d.activeReportID
	}
	features := d.featureFlags
	var report []byte
	switch reportID {
	case ReportIDCommon:
		intervalMs := uint32(d.inputIntervalMs)
		if intervalMs == 0 {
			intervalMs = 1
		}
		d.reportCounter32 += intervalMs
		var motionTS uint32
		if features&FeatureIMU != 0 {
			// A wired NS2 Pro advances both clocks from its endpoint cadence,
			// independently of host scheduling jitter. At bInterval=4 ms the
			// report counter steps by 4 and the motion clock by 4000 us.
			if d.protocolClockResample {
				// All reports use one wall-clock timebase; no calibration-only
				// fake startup clock and no cached old timestamp on held states.
				wall := protocolWallClock(time.Since(d.clockBase), d.lastWallClockUS, d.motionClockInitialized)
				d.lastWallClockUS = wall
				d.lastMotionTS = uint32(wall)
				d.reportCounter32 = uint32(wall / 1000)
			} else if d.sourcePaced {
				source := st.MotionTimestampUs
				if source != 0 || d.havePhysicalSourceClock {
					if !d.havePhysicalSourceClock {
						if !d.motionClockInitialized {
							d.sourceClockUS = uint64(source)
						} else {
							d.sourceClockUS++
						}
						d.havePhysicalSourceClock = true
					} else {
						delta := source - d.lastPhysicalSourceUS // uint32 source wrap is intentional
						if delta == 0 {
							delta = 1
						}
						d.sourceClockUS += uint64(delta)
					}
					d.lastPhysicalSourceUS = source
				} else {
					fallback := uint64(time.Since(d.clockBase).Microseconds())
					if d.motionClockInitialized && fallback <= d.sourceClockUS {
						fallback = d.sourceClockUS + 1
					}
					d.sourceClockUS = fallback
				}
				d.lastMotionTS = uint32(d.sourceClockUS)
				d.reportCounter32 = uint32(d.sourceClockUS / 1000)
			} else {
				d.lastMotionTS += intervalMs * 1000
			}
			motionTS = d.lastMotionTS
			d.motionClockInitialized = true
		}
		report = st.buildCommonReport(d.reportCounter32, motionTS, features, meta)
	default:
		d.reportCounter8++
		report = st.buildProReport(d.reportCounter8, features, meta)
	}
	d.protoMu.Unlock()
	return report
}

func protocolWallClock(elapsed time.Duration, last uint64, initialized bool) uint64 {
	tick := uint64(elapsed.Microseconds() / 4000 * 4000)
	if initialized && tick <= last {
		tick = last + 1
	}
	return tick
}

func (d *NS2Pro) signalInputReady() {
	select {
	case d.inputCh <- struct{}{}:
	default:
	}
}

func (d *NS2Pro) serialNumber() string {
	d.stateMu.Lock()
	defer d.stateMu.Unlock()
	if d.metaState == nil {
		return ""
	}
	return d.metaState.SerialNumber
}

func (d *NS2Pro) handleOutputReport(out []byte) {
	if len(out) == 0 {
		return
	}

	payload := out
	if out[0] == ReportIDOutput {
		payload = out[1:]
	} else if len(out) != OutputRumbleSize {
		return
	}
	if len(payload) < OutputRumbleSize {
		return
	}

	feedback := OutputState{}
	copy(feedback.LeftRumble[:], payload[0:16])
	copy(feedback.RightRumble[:], payload[16:32])
	feedback.Flags = OutputFlagRumble
	d.emitOutput(feedback)
}

func (d *NS2Pro) emitOutput(feedback OutputState) {
	d.outputMu.RLock()
	callback := d.outputCallback
	d.outputMu.RUnlock()
	if callback != nil {
		callback(feedback)
	}
}

func (d *NS2Pro) enqueueResponse(resp []byte) {
	d.protoMu.Lock()
	d.bulkInQueue = append(d.bulkInQueue, append([]byte(nil), resp...))
	d.protoMu.Unlock()
	select {
	case d.bulkCh <- struct{}{}:
	default:
	}
}

func (d *NS2Pro) popBulkIn() []byte {
	d.protoMu.Lock()
	defer d.protoMu.Unlock()
	if len(d.bulkInQueue) == 0 {
		return nil
	}
	chunk := d.bulkInQueue[0]
	d.bulkInQueue = d.bulkInQueue[1:]
	return append([]byte(nil), chunk...)
}

func commandHeader(cmd, seq, sub uint8) []byte {
	return []byte{cmd, 0x01, seq, sub, 0x10, 0x78, 0x00, 0x00}
}
