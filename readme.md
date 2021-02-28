MidiSplit
=========

This is a commandline tool that takes a MIDI file and splits it into a number of other MIDI files, each containing a single channel with no simultaneous keypresses. The purpose is to make it possible to separate chords and other polyphony to look better in renderings with [SidWizPlus](https://github.com/maxim-zhao/SidWizPlus).

Usage:

```
midisplit split foo.mid
```
Will split to multiple files names foo.ch<x>.<instrument>.<counter>.mid. Any polyphony will use the lowest available counter.

```
midisplit print foo.mid
```
Will print the MIDI file to the console. Redirect to a file if you like. I use this for debugging, I'm sure there are better tools out there.

```
midisplit singletrack foo.mid
```
Will rewrite to foo.singletrack.mid with all MIDI events in a single track. This is the opposite of splitting!


This program would not be possible without [DryWetMIDI](https://melanchall.github.io/drywetmidi/).