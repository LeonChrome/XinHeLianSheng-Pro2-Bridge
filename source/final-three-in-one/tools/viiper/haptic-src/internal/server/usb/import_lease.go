package usb

import (
	"context"
	"net"
	"sync"
)

type importOwner struct {
	ctx  context.Context
	conn net.Conn
}

// One imported Windows instance per exported virtual device. Different devices
// retain independent leases; closing an old lease cannot release a replacement.
func (s *Server) claimImport(key string, ctx context.Context, conn net.Conn) (func(), bool) {
	if ctx == nil || ctx.Err() != nil {
		return nil, false
	}
	s.importsMu.Lock()
	if s.imports == nil {
		s.imports = make(map[string]*importOwner)
	}
	previous := s.imports[key]
	if previous != nil && previous.ctx.Err() == nil {
		s.importsMu.Unlock()
		return nil, false
	}
	owner := &importOwner{ctx: ctx, conn: conn}
	s.imports[key] = owner
	s.importsMu.Unlock()
	if previous != nil {
		_ = previous.conn.Close()
	}
	stop := context.AfterFunc(ctx, func() { _ = conn.Close() })
	release := sync.OnceFunc(func() {
		stop()
		s.importsMu.Lock()
		if s.imports[key] == owner {
			delete(s.imports, key)
		}
		s.importsMu.Unlock()
	})
	return release, true
}
