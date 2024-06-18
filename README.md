# LidStatusService

LidStatusService is a Windows service to run a command when the laptop lid opens or closes.

This is a fork of [rowandh/lidstatusservice](https://github.com/rowandh/lidstatusservice)
(see also [its README](UPSTREAM-README.md)).

## Example use case

On open, switch Windows audio input and output to headset.
On close, switch to microphone and speakers.

## Installation

1. Build solution in Visual Studio (or Rider).
2. In Developer PowerShell: `installutil.exe path/to/built/LidStatusService.exe`

## Setup

1. In Services, start LidStatusService (and have it start automatically, if you want).
2. Write a PowerShell script that takes an argument (expect either "opened" or "closed").
3. When there's a magic location (not yet) that will cause your script to get run, put it there.

## TODO

- Stopping service should stop service
- Run PowerShell script in magic location, if it exists
- Convert to 64-bit
- Create Windows installer
- Target multiple architectures
- Document OS and library dependencies
- Add GitHub workflow