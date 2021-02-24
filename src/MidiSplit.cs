using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Standards;

namespace MidiSplit
{
    internal static class MidiSplit
    {
        static void Main(string[] args)
        {
            try
            {
                if (args.Length != 2)
                {
                    throw new Exception("Invalid parameters. Usage: midisplit <split|print> <filename>");
                }

                switch (args[0].ToLowerInvariant())
                {
                    case "print":
                        Print(args[1]);
                        return;
                    case "split":
                        Split(args[1]);
                        return;
                    default:
                        throw new Exception($"Invalid verb \"{args[0]}\"");
                }

            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Error: {e.Message}");
                Console.Error.WriteLine(e.StackTrace);
            }
        }

        private class Writer
        {
            private readonly string _instrumentName;
            private readonly TrackChunk _trackChunk;
            private long _pendingDeltaTime;

            public Writer(int instrument, string instrumentName)
            {
                _instrumentName = instrumentName;
                Instrument = instrument;
                _trackChunk = new TrackChunk();
                _pendingDeltaTime = 0;
            }

            public Writer(Writer template, int instrument, string instrumentName)
            {
                _instrumentName = instrumentName;
                Instrument = instrument;
                _trackChunk = template._trackChunk.Clone() as TrackChunk;
                _pendingDeltaTime = template._pendingDeltaTime;
            }

            public int Instrument { get; }

            public void Write(MidiEvent midiEvent)
            {
                // We add any extra delays to the event
                if (_pendingDeltaTime > 0)
                {
                    midiEvent = midiEvent.Clone();
                    midiEvent.DeltaTime += _pendingDeltaTime;
                    _pendingDeltaTime = 0;
                }

                _trackChunk.Events.Add(midiEvent);

                switch (midiEvent)
                {
                    case NoteOnEvent n:
                        Available = false;
                        NoteNumber = n.NoteNumber;
                        break;
                    case NoteOffEvent _:
                        Available = true;
                        break;
                }
            }

            public bool Available { get; private set; } = true;
            public SevenBitNumber NoteNumber { get; private set; }

            public void WriteToDisk(long totalLength, string filenamePrefix, int instrumentIndex, TimeDivision timeDivision)
            {
                var midiFile = new MidiFile(_trackChunk) {TimeDivision = timeDivision};
                var filename = $"{filenamePrefix}.{_instrumentName}";
                if (instrumentIndex > 0)
                {
                    filename += $".{instrumentIndex}";
                }
                filename += ".mid";

                var tempoMap = midiFile.GetTempoMap();
                var lastEvent = midiFile.GetTimedEvents().Last();
                if (lastEvent.Time < totalLength)
                {
                    _trackChunk.Events.Add(new ControlChangeEvent{DeltaTime = totalLength - lastEvent.Time});
                }

                midiFile.Write(filename, true, MidiFileFormat.SingleTrack);
                Console.WriteLine($"Wrote to {filename}, duration {(TimeSpan)midiFile.GetTimedEvents().Last().TimeAs<MetricTimeSpan>(tempoMap)}");
            }

            public void AddDelay(long deltaTime)
            {
                _pendingDeltaTime += deltaTime;
            }

            public long GetLength()
            {
                return _trackChunk.GetTimedEvents().Last().Time;
            }
        }

        /// <summary>
        /// Handles all updates for a given channel
        /// </summary>
        private class ChannelHandler
        {
            private readonly int _channelNumber;
            private int _instrument = -1;
            private readonly List<Writer> _writers = new List<Writer>();
            private readonly Writer _emptyWriter;

            public ChannelHandler(int channelNumber)
            {
                _channelNumber = channelNumber;
                // Create an "empty" handler for instrument -1
                _emptyWriter = new Writer(-1, "Empty");
                _writers.Add(_emptyWriter);
            }

            public void Write(MidiEvent midiEvent)
            {
                if (midiEvent is ChannelEvent c)
                {
                    if (c.Channel != _channelNumber)
                    {
                        // Ignore it
                        return;
                    }

                    switch (c)
                    {
                        case ProgramChangeEvent p:
                            _instrument = p.ProgramNumber;
                            break;
                        case NoteOnEvent n:
                        {
                            // Add it to a writer, creating a new one if there is none
                            // If we are the percussion channel, the note defines the instrument
                            if (_channelNumber == 9)
                            {
                                _instrument = n.NoteNumber;
                            }
                            var writer = _writers.FirstOrDefault(x => x.Available && x.Instrument == _instrument);
                            if (writer == null)
                            {
                                // Make a new one
                                // If we are the percussion channel, the note defines the instrument
                                if (_channelNumber == 9)
                                {
                                    writer = new Writer(_emptyWriter, _instrument, ((GeneralMidiPercussion)_instrument).ToString());
                                }
                                else
                                {
                                    writer = new Writer(_emptyWriter, _instrument, ((GeneralMidiProgram)_instrument).ToString());
                                }
                                _writers.Add(writer);
                            }
                            // Write it
                            writer.Write(n);
                            // Add a delay to the rest
                            foreach (var writer1 in _writers.Where(x => x != writer))
                            {
                                writer1.AddDelay(n.DeltaTime);
                            }
                            return;
                        }
                        case NoteOffEvent n:
                        {
                            // Find the writer with a matching note
                            var writer = _writers.First(x => x.NoteNumber == n.NoteNumber);
                            writer.Write(n);
                            // Add a delay to the rest
                            foreach (var writer1 in _writers.Where(x => x != writer))
                            {
                                writer1.AddDelay(n.DeltaTime);
                            }
                            return;
                        }
                    }
                }

                // If not early-returned above, write to all writers here
                foreach (var writer in _writers)
                {
                    writer.Write(midiEvent);
                }
            }

            public long GetMaxLength()
            {
                return _writers.Max(x => x.GetLength());
            }

            public void WriteToDisk(long maxLength, string filenamePrefix, TimeDivision timeDivision)
            {
                var countsPerInstrument = _writers
                    .GroupBy(x => x.Instrument)
                    .ToDictionary(
                        x => x.Key,
                        x => x.Count());
                var currentIndexByInstrument = _writers
                    .Select(x => x.Instrument)
                    .Distinct()
                    .ToDictionary(
                        x => x,
                        x => 1);
                foreach (var writer in _writers.Where(x => x != _emptyWriter))
                {
                    if (countsPerInstrument[writer.Instrument] == 1)
                    {
                        writer.WriteToDisk(maxLength, $"{filenamePrefix}.ch{_channelNumber}", -1, timeDivision);
                    }
                    else
                    {
                        var index = currentIndexByInstrument[writer.Instrument];
                        writer.WriteToDisk(maxLength, $"{filenamePrefix}.ch{_channelNumber}", index, timeDivision);
                        currentIndexByInstrument[writer.Instrument] = index + 1;
                    }
                }
            }
        }

        static void Split(string filename)
        {
            // We open the file...
            var f = MidiFile.Read(filename);

            // We create channel handlers for each channel in the file...
            var channels = f.GetChannels().ToDictionary(
                x => x, 
                x => new ChannelHandler(x));

            // Then we parse the file and filter into these handlers
            foreach (var e in f.GetTrackChunks().SelectMany(chunk => chunk.Events))
            {
                if (e is ChannelEvent channelEvent)
                {
                    // Pass to the specific channel handler
                    channels[channelEvent.Channel].Write(e);
                }
                else
                {
                    // Pass everything else through to all channels handlers
                    foreach (var channelHandler in channels.Values) 
                    {
                        channelHandler.Write(e);
                    }
                }
            }

            // Then pad them all to the same length
            var maxLength = channels.Values.Max(x => x.GetMaxLength());

            foreach (var writer in channels.Values)
            {
                writer.WriteToDisk(
                    maxLength, 
                    Path.Combine(
                        Path.GetDirectoryName(filename) ?? "",
                        Path.GetFileNameWithoutExtension(filename)),
                    f.TimeDivision);
            }
        }
        
        static void Print(string filename)
        {
            // We open the file...
            var f = MidiFile.Read(filename);

            Console.WriteLine($"Chunks: {f.Chunks.Count}");
            Console.WriteLine($"Format: {f.OriginalFormat}");
            Console.WriteLine($"Time division: {f.TimeDivision}");

            var channelTimes = Enumerable.Repeat(0L, 255).ToList();
            foreach (var trackChunk in f.GetTrackChunks())
            {
                foreach (var e in trackChunk.Events)
                {
                    switch (e)
                    {
                        case CopyrightNoticeEvent c:
                            Console.WriteLine($"CopyrightNotice {c.Text}");
                            break;
                        case SequenceTrackNameEvent n:
                            Console.WriteLine($"SequenceTrackName {n.Text}");
                            break;
                        case KeySignatureEvent k:
                            Console.WriteLine($"KeySignature key={k.Key} scale={k.Scale}");
                            break;
                        case SetTempoEvent t:
                            Console.WriteLine($"SetTempo {t.MicrosecondsPerQuarterNote}us per quarter note");
                            break;
                        case ChannelEvent c:
                            channelTimes[c.Channel] += c.DeltaTime;
                            Console.Write($"[{c.Channel}] +{c.DeltaTime:D4} [{channelTimes[c.Channel]:D6}] {c.EventType} ");
                            switch (c)
                            {
                                case ProgramChangeEvent p:
                                    Console.WriteLine($"{p.ProgramNumber} ({(GeneralMidiProgram) (int) p.ProgramNumber})");
                                    break;
                                case ControlChangeEvent cc:
                                    Console.WriteLine($"{cc.GetControlName()}={cc.ControlValue}");
                                    break;
                                case NoteOnEvent n:
                                    Console.WriteLine($"{n.GetNoteName()} {n.NoteNumber}");
                                    break;
                                case NoteOffEvent n:
                                    Console.WriteLine($"{n.GetNoteName()} {n.NoteNumber}");
                                    break;
                                case PitchBendEvent b:
                                    Console.WriteLine($"{b.PitchValue}");
                                    break;
                                default:
                                    Console.WriteLine("######");
                                    break;
                            }
                            break;
                        default:
                            Console.WriteLine($"Event {e.EventType} ######");
                            break;
                    }
                }
            }
        }

    }
}
