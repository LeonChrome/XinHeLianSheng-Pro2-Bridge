package device

import (
	"context"
	"sync"
	"time"
)

// StreamLifetime owns idle removal for a device, rather than for one TCP stream.
type StreamLifetime struct {
	mu               sync.Mutex
	next, generation uint64
	active           map[uint64]struct{}
	timer            *time.Timer
	closed           bool
}

func NewStreamLifetime() *StreamLifetime { return &StreamLifetime{active: make(map[uint64]struct{})} }

func (s *StreamLifetime) Acquire() (uint64, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.closed {
		return 0, false
	}
	s.generation++
	if s.timer != nil {
		s.timer.Stop()
		s.timer = nil
	}
	s.next++
	s.active[s.next] = struct{}{}
	return s.next, true
}

func (s *StreamLifetime) Release(lease uint64, delay time.Duration, remove func()) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, ok := s.active[lease]; !ok {
		return
	}
	delete(s.active, lease)
	if len(s.active) == 0 {
		s.armLocked(delay, remove)
	}
}

func (s *StreamLifetime) ArmIdle(delay time.Duration, remove func()) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.armLocked(delay, remove)
}

func (s *StreamLifetime) armLocked(delay time.Duration, remove func()) {
	if s.closed || len(s.active) != 0 {
		return
	}
	s.generation++
	generation := s.generation
	if s.timer != nil {
		s.timer.Stop()
	}
	s.timer = time.AfterFunc(delay, func() {
		s.mu.Lock()
		defer s.mu.Unlock()
		if s.closed || s.generation != generation || len(s.active) != 0 {
			return
		}
		// Keep the check and deletion atomic with respect to acquiring a lease.
		s.closed = true
		s.timer = nil
		remove()
	})
}

func (s *StreamLifetime) Close() {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.closed = true
	s.generation++
	if s.timer != nil {
		s.timer.Stop()
		s.timer = nil
	}
}

func GetStreamLifetime(ctx context.Context) *StreamLifetime {
	value, _ := ctx.Value(StreamLifetimeKey).(*StreamLifetime)
	return value
}
