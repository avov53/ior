package main

import (
	"bytes"
	"encoding/binary"
	"encoding/hex"
	"fmt"
	"github.com/amnezia-vpn/amneziawg-go/v3/conn"
	"github.com/amnezia-vpn/amneziawg-go/v3/device"
	"github.com/amnezia-vpn/amneziawg-go/v3/tun"
	"github.com/amnezia-vpn/amneziawg-go/v3/tun/tuntest"
	"golang.org/x/crypto/curve25519"
	"net/netip"
	"os"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"
)

type memoryTun struct {
	outgoing chan []byte
	incoming chan []byte
	events   chan tun.Event
	done     chan struct{}
	ready    chan struct{}
	primed   bool
	once     sync.Once
}

func newMemoryTun() *memoryTun {
	return &memoryTun{outgoing: make(chan []byte, 8), incoming: make(chan []byte, 8), events: make(chan tun.Event), done: make(chan struct{}), ready: make(chan struct{})}
}
func (t *memoryTun) File() *os.File           { return nil }
func (t *memoryTun) Name() (string, error)    { return "test", nil }
func (t *memoryTun) MTU() (int, error)        { return 1380, nil }
func (t *memoryTun) BatchSize() int           { return 1 }
func (t *memoryTun) Events() <-chan tun.Event { return t.events }
func (t *memoryTun) Close() error             { t.once.Do(func() { close(t.done); close(t.events) }); return nil }
func (t *memoryTun) Read(bufs [][]byte, sizes []int, offset int) (int, error) {
	if !t.primed {
		select {
		case <-t.ready:
			t.primed = true
			return 0, nil
		case <-t.done:
			return 0, os.ErrClosed
		}
	}
	select {
	case packet := <-t.outgoing:
		sizes[0] = copy(bufs[0][offset:], packet)
		return 1, nil
	case <-t.done:
		return 0, os.ErrClosed
	}
}
func (t *memoryTun) Write(bufs [][]byte, offset int) (int, error) {
	for _, buf := range bufs {
		select {
		case t.incoming <- append([]byte{}, buf[offset:]...):
		case <-t.done:
			return 0, os.ErrClosed
		}
	}
	return len(bufs), nil
}

func TestOfficialAWG31EncryptedObfuscatedRoundTrip(t *testing.T) { officialRoundTrip(t, false) }
func TestOfficialAWG31IPv6EndpointAndInnerPacket(t *testing.T)   { officialRoundTrip(t, true) }
func officialRoundTrip(t *testing.T, outer6 bool) {
	var keys [2][32]byte
	for i := range keys {
		for j := range keys[i] {
			keys[i][j] = byte(11 + i + j)
		}
	}
	var pubs [2][]byte
	for i := range keys {
		pub, err := curve25519.X25519(keys[i][:], curve25519.Basepoint)
		if err != nil {
			t.Fatal(err)
		}
		pubs[i] = pub
	}
	var engines [2]*device.Device
	var tuns [2]*memoryTun
	var ports [2]int
	// Non-WireGuard headers, handshake padding and AWG 3.1 content padding.
	obfs := "jc=2\njmin=12\njmax=24\ns1=16\ns2=16\ns3=4\ns4=4\nh1=12345678\nh2=23456789\nh3=34567890\nh4=45678901\ncontent_padding_addition=8-16\nrandom_trailers=true\n"
	for i := 0; i < 2; i++ {
		tuns[i] = newMemoryTun()
		engines[i] = device.NewDevice(tuns[i], conn.NewStdNetBind(), &device.Logger{Verbosef: func(string, ...any) {}, Errorf: func(string, ...any) {}})
		defer engines[i].Close()
		config := obfs + fmt.Sprintf("private_key=%s\nlisten_port=0\nreplace_peers=true\npublic_key=%s\nprotocol_version=1\nallowed_ip=10.7.0.%d/32\nallowed_ip=fd00:7::%d/128\n", hex.EncodeToString(keys[i][:]), hex.EncodeToString(pubs[1-i]), 2-i, 2-i)
		if err := engines[i].IpcSet(config); err != nil {
			t.Fatal("configuration rejected")
		}
		if err := engines[i].Up(); err != nil {
			t.Fatal(err)
		}
		close(tuns[i].ready)
		status, err := engines[i].IpcGet()
		if err != nil {
			t.Fatal(err)
		}
		for _, line := range strings.Split(status, "\n") {
			if strings.HasPrefix(line, "listen_port=") {
				ports[i], err = strconv.Atoi(strings.TrimPrefix(line, "listen_port="))
				if err != nil {
					t.Fatal(err)
				}
			}
		}
		if ports[i] == 0 {
			t.Fatal("no UDP endpoint")
		}
	}
	host := "127.0.0.1"
	if outer6 {
		host = "[::1]"
	}
	for i := 0; i < 2; i++ {
		if err := engines[i].IpcSet(fmt.Sprintf("public_key=%s\nendpoint=%s:%d\n", hex.EncodeToString(pubs[1-i]), host, ports[1-i])); err != nil {
			t.Fatal(err)
		}
	}
	for i := 0; i < 2; i++ {
		packet := tuntest.Ping(netip.MustParseAddr(fmt.Sprintf("10.7.0.%d", 2-i)), netip.MustParseAddr(fmt.Sprintf("10.7.0.%d", i+1)))
		tuns[i].outgoing <- packet
		select {
		case received := <-tuns[1-i].incoming:
			if !bytes.Equal(packet, received) {
				t.Fatal("packet changed")
			}
		case <-time.After(8 * time.Second):
			t.Fatal("AWG handshake/packet timed out")
		}
	}
	for i := 0; i < 2; i++ {
		packet := make([]byte, 52)
		packet[0] = 0x60
		packet[5] = 12
		packet[6] = 17
		packet[7] = 64
		source := netip.MustParseAddr(fmt.Sprintf("fd00:7::%d", i+1)).As16()
		destination := netip.MustParseAddr(fmt.Sprintf("fd00:7::%d", 2-i)).As16()
		copy(packet[8:24], source[:])
		copy(packet[24:40], destination[:])
		copy(packet[40:], []byte{0x12, 0x34, 0x23, 0x45, 0, 12, 0, 0, 1, 2, 3, 4})
		pseudo := append([]byte{}, packet[8:40]...)
		pseudo = append(pseudo, 0, 0, 0, 12, 0, 0, 0, 17)
		pseudo = append(pseudo, packet[40:]...)
		sum := uint32(0)
		for j := 0; j < len(pseudo); j += 2 {
			sum += uint32(binary.BigEndian.Uint16(pseudo[j : j+2]))
		}
		for sum > 65535 {
			sum = (sum & 65535) + (sum >> 16)
		}
		binary.BigEndian.PutUint16(packet[46:48], ^uint16(sum))
		tuns[i].outgoing <- packet
		select {
		case received := <-tuns[1-i].incoming:
			if !bytes.Equal(packet, received) {
				t.Fatal("IPv6 packet changed")
			}
		case <-time.After(8 * time.Second):
			t.Fatal("IPv6 AWG timed out")
		}
	}
}
