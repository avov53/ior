// ior adapter for official AmneziaWG 3.1.20260828. Configuration and raw IP
// packets use inherited private pipes; no keys appear in command arguments.
package main

import (
	"bufio"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/amnezia-vpn/amneziawg-go/v3/conn"
	"github.com/amnezia-vpn/amneziawg-go/v3/device"
	"github.com/amnezia-vpn/amneziawg-go/v3/tun"
)

type configuration struct {
	API             int    `json:"api"`
	UAPI            string `json:"uapi"`
	InterfaceIndex  uint32 `json:"interfaceIndex"`
	InterfaceIndex6 uint32 `json:"interfaceIndex6"`
	EndpointV6      bool   `json:"endpointV6"`
	MTU             int    `json:"mtu"`
}
type pipeTun struct {
	input  *bufio.Reader
	output io.Writer
	mutex  sync.Mutex
	mtu    int
	events chan tun.Event
	closed chan struct{}
	ready  chan struct{}
	primed bool
	once   sync.Once
}

func (t *pipeTun) File() *os.File           { return nil }
func (t *pipeTun) Name() (string, error)    { return "ior-awg-internal", nil }
func (t *pipeTun) MTU() (int, error)        { return t.mtu, nil }
func (t *pipeTun) BatchSize() int           { return 1 }
func (t *pipeTun) Events() <-chan tun.Event { return t.events }
func (t *pipeTun) Close() error {
	t.once.Do(func() { close(t.closed); close(t.events); os.Stdin.Close() })
	return nil
}
func (t *pipeTun) Read(bufs [][]byte, sizes []int, offset int) (int, error) {
	// NewDevice starts its reader before IpcSet and captures S4's old offset.
	// End that first empty read after configuration; the next read uses S4.
	if !t.primed {
		select {
		case <-t.ready:
			t.primed = true
			return 0, nil
		case <-t.closed:
			return 0, os.ErrClosed
		}
	}
	var length uint32
	if err := binary.Read(t.input, binary.BigEndian, &length); err != nil {
		return 0, err
	}
	if length < 20 || length > 65535 || int(length) > len(bufs[0])-offset {
		return 0, fmt.Errorf("AWG_PACKET_LENGTH_INVALID")
	}
	if _, err := io.ReadFull(t.input, bufs[0][offset:offset+int(length)]); err != nil {
		return 0, err
	}
	sizes[0] = int(length)
	return 1, nil
}
func (t *pipeTun) Write(bufs [][]byte, offset int) (int, error) {
	t.mutex.Lock()
	defer t.mutex.Unlock()
	for i, buf := range bufs {
		if offset < 0 || offset > len(buf) {
			return i, fmt.Errorf("AWG_PACKET_LENGTH_INVALID")
		}
		buf = buf[offset:]
		if len(buf) < 20 || len(buf) > 65535 {
			return i, fmt.Errorf("AWG_PACKET_LENGTH_INVALID")
		}
		var header [4]byte
		binary.BigEndian.PutUint32(header[:], uint32(len(buf)))
		if err := writeAll(t.output, header[:]); err != nil {
			return i, err
		}
		if err := writeAll(t.output, buf); err != nil {
			return i, err
		}
	}
	return len(bufs), nil
}
func writeAll(writer io.Writer, data []byte) error {
	for len(data) > 0 {
		n, err := writer.Write(data)
		if n < 0 || n > len(data) {
			return io.ErrShortWrite
		}
		data = data[n:]
		if err != nil {
			return err
		}
		if n == 0 {
			return io.ErrShortWrite
		}
	}
	return nil
}
func run() error {
	input := bufio.NewReader(os.Stdin)
	line, err := input.ReadBytes('\n')
	if err != nil {
		return err
	}
	if len(line) > 65536 {
		return fmt.Errorf("AWG_CONFIG_TOO_LARGE")
	}
	var config configuration
	decoder := json.NewDecoder(strings.NewReader(string(line)))
	decoder.DisallowUnknownFields()
	if err = decoder.Decode(&config); err != nil {
		return fmt.Errorf("AWG_CONFIG_INVALID")
	}
	if config.API != 2 || config.MTU < 1280 || config.MTU > 1500 || (config.InterfaceIndex == 0 && config.InterfaceIndex6 == 0) || (config.EndpointV6 && config.InterfaceIndex6 == 0) {
		return fmt.Errorf("AWG_CONFIG_INVALID")
	}
	t := &pipeTun{input: input, output: os.Stdout, mtu: config.MTU, events: make(chan tun.Event, 1), closed: make(chan struct{}), ready: make(chan struct{})}
	bind := conn.NewDefaultBind()
	binding, ok := bind.(conn.BindSocketToInterface)
	if !ok {
		return fmt.Errorf("AWG_PHYSICAL_BIND_UNSUPPORTED")
	}
	// Errors from the official component must never print the supplied UAPI text.
	logger := &device.Logger{Verbosef: func(string, ...any) {}, Errorf: func(string, ...any) { fmt.Fprintln(os.Stderr, "AWG_ENGINE_IO_ERROR") }}
	engine := device.NewDevice(t, bind, logger)
	defer engine.Close()
	if err = engine.IpcSet(config.UAPI); err != nil {
		return fmt.Errorf("AWG_PROFILE_REJECTED")
	}
	if err = engine.Up(); err != nil {
		return fmt.Errorf("AWG_ENGINE_START_FAILED")
	}
	if config.InterfaceIndex != 0 {
		if err = binding.BindSocketToInterface4(config.InterfaceIndex, false); err != nil {
			return fmt.Errorf("AWG_PHYSICAL_BIND_FAILED")
		}
	}
	if config.InterfaceIndex6 != 0 {
		if err = binding.BindSocketToInterface6(config.InterfaceIndex6, false); err != nil {
			return fmt.Errorf("AWG_PHYSICAL_BIND6_FAILED")
		}
	}
	close(t.ready)
	fmt.Fprintln(os.Stderr, "AWG_ADAPTER_READY")
	go reportMetrics(engine)
	<-engine.Wait()
	return nil
}
func reportMetrics(engine *device.Device) {
	start := time.Now()
	timer := time.NewTicker(250 * time.Millisecond)
	defer timer.Stop()
	var handshake, rx, tx uint64
	for {
		select {
		case <-engine.Wait():
			return
		case <-timer.C:
			status, err := engine.IpcGet()
			if err != nil {
				continue
			}
			var nextHandshake, nextRx, nextTx uint64
			// The raw UAPI response contains keys: only these three numeric fields may leave this function.
			for _, line := range strings.Split(status, "\n") {
				key, value, ok := strings.Cut(line, "=")
				if !ok {
					continue
				}
				switch key {
				case "last_handshake_time_sec":
					nextHandshake, _ = strconv.ParseUint(value, 10, 64)
				case "rx_bytes":
					nextRx, _ = strconv.ParseUint(value, 10, 64)
				case "tx_bytes":
					nextTx, _ = strconv.ParseUint(value, 10, 64)
				}
			}
			if nextHandshake > handshake {
				fmt.Fprintf(os.Stderr, "{\"code\":\"AWG_HANDSHAKE_AUTHENTICATED\",\"elapsedMs\":%d}\n", time.Since(start).Milliseconds())
				handshake = nextHandshake
			}
			if nextRx != rx || nextTx != tx {
				fmt.Fprintf(os.Stderr, "{\"code\":\"AWG_TRAFFIC_METRICS\",\"sentBytes\":%d,\"receivedBytes\":%d}\n", nextTx, nextRx)
				rx = nextRx
				tx = nextTx
			}
		}
	}
}
func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "AWG_ADAPTER_FAILED")
		os.Exit(1)
	}
}
