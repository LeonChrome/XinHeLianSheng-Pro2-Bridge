//go:build !release

package config

import "github.com/Alia5/VIIPER/internal/cmd"

type codegenCommand struct {
	Codegen cmd.Codegen `cmd:"" help:"Generate client libraries from server code"`
}
