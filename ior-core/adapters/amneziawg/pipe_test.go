package main

import (
	"bufio"
	"bytes"
	"encoding/binary"
	"errors"
	"io"
	"os"
	"testing"
)

type singleByteReader struct{ io.Reader }

func (r singleByteReader) Read(p []byte) (int, error) { return r.Reader.Read(p[:1]) }

type failedPacketWriter struct{ calls int }
type partialWriter struct{ bytes.Buffer }

func (w *partialWriter) Write(p []byte) (int, error) {
	if len(p) > 3 {
		p = p[:3]
	}
	return w.Buffer.Write(p)
}
func TestPartialWritesPreserveCompletePacket(t *testing.T) {
	w := &partialWriter{}
	packet := bytes.Repeat([]byte{42}, 1280)
	tun := &pipeTun{output: w}
	if n, err := tun.Write([][]byte{packet}, 0); n != 1 || err != nil {
		t.Fatal(n, err)
	}
	if binary.BigEndian.Uint32(w.Bytes()[:4]) != 1280 || !bytes.Equal(w.Bytes()[4:], packet) {
		t.Fatal("incomplete frame")
	}
}

func (w *failedPacketWriter) Write(p []byte) (int, error) {
	w.calls++
	if w.calls == 2 {
		return 0, io.ErrShortWrite
	}
	return len(p), nil
}

func TestPipePacketsPreserveFramingOrderOffsetsAndPartialReads(t *testing.T) {
	packets := [][]byte{bytes.Repeat([]byte{1}, 20), bytes.Repeat([]byte{2}, 1380), bytes.Repeat([]byte{3}, 1500)}
	var framed bytes.Buffer
	writer := &pipeTun{output: &framed}
	var buffers [][]byte
	for _, packet := range packets {
		buffers = append(buffers, append(make([]byte, 4), packet...))
	}
	if n, err := writer.Write(buffers, 4); err != nil || n != len(packets) {
		t.Fatalf("write: %d %v", n, err)
	}
	reader := &pipeTun{input: bufio.NewReader(singleByteReader{&framed}), primed: true}
	for _, packet := range packets {
		buffer := make([]byte, 1600)
		sizes := make([]int, 1)
		if n, err := reader.Read([][]byte{buffer}, sizes, 8); err != nil || n != 1 {
			t.Fatalf("read: %d %v", n, err)
		}
		if !bytes.Equal(packet, buffer[8:8+sizes[0]]) {
			t.Fatal("packet changed or reordered")
		}
	}
	if _, err := reader.Read([][]byte{make([]byte, 1600)}, make([]int, 1), 0); !errors.Is(err, io.EOF) {
		t.Fatalf("EOF: %v", err)
	}
}

func TestPipeRejectsTruncationInvalidLengthAndFailedWrite(t *testing.T) {
	for _, length := range []uint32{0, 19, 65536, 40} {
		var framed bytes.Buffer
		binary.Write(&framed, binary.BigEndian, length)
		framed.Write(make([]byte, 20))
		reader := &pipeTun{input: bufio.NewReader(&framed), primed: true}
		if _, err := reader.Read([][]byte{make([]byte, 1500)}, make([]int, 1), 0); err == nil {
			t.Fatalf("accepted invalid frame %d", length)
		}
	}
	writer := &pipeTun{output: &failedPacketWriter{}}
	if n, err := writer.Write([][]byte{make([]byte, 20)}, 0); n != 0 || !errors.Is(err, io.ErrShortWrite) {
		t.Fatalf("write error: %d %v", n, err)
	}
	closed := make(chan struct{})
	close(closed)
	reader := &pipeTun{ready: make(chan struct{}), closed: closed}
	if _, err := reader.Read(nil, nil, 0); !errors.Is(err, os.ErrClosed) {
		t.Fatalf("shutdown: %v", err)
	}
}
