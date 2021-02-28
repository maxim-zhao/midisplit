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
                    throw new Exception("Invalid parameters. Usage: midisplit <split|print|singletrack> <filename>");
                }

                switch (args[0].ToLowerInvariant())
                {
                    case "print":
                        Print(args[1]);
                        return;
                    case "split":
                        Split(args[1]);
                        return;
                    case "singletrack":
                        SingleTrack(args[1]);
                        break;
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
                        // Just add the delay
                        foreach (var writer in _writers)
                        {
                            writer.AddDelay(c.DeltaTime);
                        }

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
            var merged = new TrackChunk();
            using (var mergedTimedEvents = merged.ManageTimedEvents())
            {
                foreach (var chunk in f.GetTrackChunks())
                {
                    Console.WriteLine($"Adding {chunk.Events.Count} events from chunk...");
                    using var m = chunk.ManageTimedEvents();
                    mergedTimedEvents.Events.Add(m.Events);
                }
                mergedTimedEvents.SaveChanges();
                Console.WriteLine($"Total events is now {mergedTimedEvents.Events.Count()}, length = {(TimeSpan)mergedTimedEvents.Events.Last().TimeAs<MetricTimeSpan>(f.GetTempoMap())}");
            }

            var maxTime = merged.Events.Sum(x => x.DeltaTime);
            Console.WriteLine($"Total {merged.Events.Count} events, time {maxTime} = {(TimeSpan)TimeConverter.ConvertTo<MetricTimeSpan>(maxTime, f.GetTempoMap())}");

            foreach (var e in merged.Events)
            {
                // Pass everything else through to all channels handlers
                foreach (var channelHandler in channels.Values) 
                {
                    channelHandler.Write(e);
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

            foreach (var trackChunk in f.GetTrackChunks())
            {
                Console.WriteLine("============= Track chunk start =============");
                var time = 0L;
                foreach (var e in trackChunk.Events)
                {
                    // These all have time
                    time += e.DeltaTime;
                    if (e is ChannelEvent c)
                    {
                        Console.Write($"+{c.DeltaTime:D4} [{time:D6}] [{c.Channel}] {c.EventType} ");
                        switch (c)
                        {
                            case ProgramChangeEvent p:
                                Console.WriteLine(
                                    $"{p.ProgramNumber} ({(GeneralMidiProgram) (int) p.ProgramNumber})");
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
                                Console.WriteLine(e.ToString());
                                break;
                        }
                    }
                    else
                    {
                        Console.WriteLine($"+{e.DeltaTime:D4} [{time:D6}] {e}");
                    }
                }
            }
        }

        private static void SingleTrack(string filename)
        {
            // We open the file...
            var f = MidiFile.Read(filename);

            // Then we convert all the events to absolute time and merge them together
            var merged = new TrackChunk();
            using (var mergedTimedEvents = merged.ManageTimedEvents())
            {
                foreach (var chunk in f.GetTrackChunks())
                {
                    using var m = chunk.ManageTimedEvents();
                    mergedTimedEvents.Events.Add(m.Events);
                }
                mergedTimedEvents.SaveChanges();
            }

            // And save
            var outFile = new MidiFile {TimeDivision = f.TimeDivision, Chunks = {merged}};
            outFile.Write(Path.Join(
                Path.GetDirectoryName(filename),
                $"{Path.GetFileNameWithoutExtension(filename)}.singletrack.mid"),
                overwriteFile:true,
                format:MidiFileFormat.SingleTrack);
        }

    }
}
