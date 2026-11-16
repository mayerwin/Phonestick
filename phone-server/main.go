// psftp: the file server Phonestick runs on the phone, through adb, while a drive is mounted.
//
// It speaks SFTP on stdin and stdout and serves the real files of one folder (the phone's
// shared storage, or its SD card) with the adb shell's rights. Reads, writes at any offset,
// renames and deletes go straight to the files, so the drive behaves like a USB stick: apps
// can edit in place, and a write is on the phone when it is acknowledged.
//
// It opens no port and keeps no state: it ends when the PC closes the connection.
package main

import (
	"io"
	"log"
	"os"

	"github.com/pkg/sftp"
)

func main() {
	log.SetFlags(0)
	if len(os.Args) != 2 {
		log.Fatal("usage: psftp FOLDER")
	}
	root := os.Args[1]
	if err := os.Chdir(root); err != nil {
		log.Fatal(err)
	}
	stdio := struct {
		io.Reader
		io.WriteCloser
	}{os.Stdin, os.Stdout}
	server, err := sftp.NewServer(stdio, sftp.WithServerWorkingDirectory(root))
	if err != nil {
		log.Fatal(err)
	}
	if err := server.Serve(); err != nil && err != io.EOF {
		log.Fatal(err)
	}
}
