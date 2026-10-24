package device

import (
	"log/slog"
	"sync"
	"time"
)

// SourcePacedInput forbids host polling from manufacturing new physical samples.
// Non-source-paced/legacy devices keep the USBIP cached-report compatibility path.
type SourcePacedInput interface{ SourcePacedInput() bool }

// Historical transitions older than this deadline yield to the newest state.
// The latest held/released state is always retained, even when it is older.
const MaxHistoricalInputAge = 100 * time.Millisecond

type timedInput[T any] struct {
	Value   T
	Arrived time.Time
}

// InputQueueStats deliberately distinguishes source updates from USB reports.
type InputQueueStats struct {
	Received     uint64 `json:"source_received"`
	Delivered    uint64 `json:"source_delivered"`
	Coalesced    uint64 `json:"analog_coalesced"`
	Overflow     uint64 `json:"edge_overflow"`
	Repeated     uint64 `json:"compatibility_repeated"`
	StaleDropped uint64 `json:"stale_dropped"`
	Depth        int    `json:"queue_depth"`
	MaxDepth     int    `json:"queue_max_depth"`
}

// InputQueue preserves digital transitions and merges only adjacent analog-only
// updates. Its finite capacity prevents an unresponsive host growing memory or
// replaying seconds of stale motion. Overflow is observable, never called lossless.
type InputQueue[T any] struct {
	mu          sync.Mutex
	values      []timedInput[T]
	capacity    int
	sameDigital func(T, T) bool
	wake        chan struct{}
	stats       InputQueueStats
	seedPending bool
	label       string
	clockPolicy string
	lastLog     time.Time
	now         func() time.Time
}

func NewInputQueue[T any](capacity int, sameDigital func(T, T) bool) *InputQueue[T] {
	return newInputQueueWithClock(capacity, sameDigital, time.Now)
}

func newInputQueueWithClock[T any](capacity int, sameDigital func(T, T) bool, now func() time.Time) *InputQueue[T] {
	if capacity < 2 {
		capacity = 2
	}
	if now == nil {
		now = time.Now
	}
	return &InputQueue[T]{capacity: capacity, sameDigital: sameDigital, wake: make(chan struct{}, 1), now: now}
}
func (q *InputQueue[T]) Push(v T) {
	q.mu.Lock()
	q.stats.Received++
	if q.seedPending {
		q.values = nil
		q.seedPending = false
	}
	n := len(q.values)
	entry := timedInput[T]{Value: v, Arrived: q.now()}
	if n > 0 && q.sameDigital(q.values[n-1].Value, v) {
		q.values[n-1] = entry
		q.stats.Coalesced++
	} else {
		if n == q.capacity {
			copy(q.values, q.values[1:])
			q.values = q.values[:n-1]
			q.stats.Overflow++
		}
		q.values = append(q.values, entry)
	}
	q.stats.MaxDepth = max(q.stats.MaxDepth, len(q.values))
	q.logLocked()
	q.mu.Unlock()
	select {
	case q.wake <- struct{}{}:
	default:
	}
}
func (q *InputQueue[T]) Pop() (T, bool) {
	q.mu.Lock()
	defer q.mu.Unlock()
	if len(q.values) == 0 {
		var zero T
		return zero, false
	}
	// Preserve prompt edges, but never replay an old press after a slow host
	// already has a newer release/current state. Bound age as well as memory.
	now := q.now()
	for len(q.values) > 1 && now.Sub(q.values[0].Arrived) >= MaxHistoricalInputAge {
		copy(q.values, q.values[1:])
		q.values = q.values[:len(q.values)-1]
		q.stats.StaleDropped++
	}
	v := q.values[0].Value
	copy(q.values, q.values[1:])
	q.values = q.values[:len(q.values)-1]
	if !q.seedPending {
		q.stats.Delivered++
	}
	q.seedPending = false
	q.logLocked()
	return v, true
}

// Seed supplies a neutral handshake state, not a physical source update.
func (q *InputQueue[T]) Seed(v T) {
	q.mu.Lock()
	q.values = []timedInput[T]{{Value: v, Arrived: q.now()}}
	q.seedPending = true
	q.stats.MaxDepth = max(q.stats.MaxDepth, 1)
	q.mu.Unlock()
	select {
	case q.wake <- struct{}{}:
	default:
	}
}
func (q *InputQueue[T]) Wake() <-chan struct{} { return q.wake }
func (q *InputQueue[T]) DiscardStale() {
	q.mu.Lock()
	if !q.seedPending {
		q.stats.StaleDropped += uint64(len(q.values))
	}
	q.values = nil
	q.seedPending = false
	q.mu.Unlock()
}
func (q *InputQueue[T]) RecordRepeat() { q.mu.Lock(); q.stats.Repeated++; q.logLocked(); q.mu.Unlock() }
func (q *InputQueue[T]) SetTelemetry(label, clock string) {
	q.mu.Lock()
	q.label = label
	q.clockPolicy = clock
	q.mu.Unlock()
}
func (q *InputQueue[T]) logLocked() {
	if q.label == "" || (!q.lastLog.IsZero() && time.Since(q.lastLog) < 5*time.Second) {
		return
	}
	q.lastLog = time.Now()
	s := q.stats
	slog.Info("[VIIPER_INPUT_PIPELINE]", "mode", q.label, "source_scope", "upstream_state_updates_not_physical_fd2", "source_received", s.Received, "source_unique", s.Delivered, "held_repeat", s.Repeated, "coalesced", s.Coalesced, "edge_drop", s.Overflow, "stale_drop", s.StaleDropped, "queue_depth", len(q.values), "queue_max", s.MaxDepth, "clock", q.clockPolicy)
}
func (q *InputQueue[T]) Stats() InputQueueStats {
	q.mu.Lock()
	defer q.mu.Unlock()
	s := q.stats
	s.Depth = len(q.values)
	return s
}
