using System;
using System.Collections.Generic;
using System.Linq;
using CommandLine;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Standards;

namespace MidiSplit
{
    internal static class MidiSplit
    {
        // ReSharper disable once ClassNeverInstantiated.Local
        private class Options
        {
            [Option(Required = true)] 
            public string Filename { get; set; }

            [Option(Required = true)] 
            public string Action { get; set; }
        }

        static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args)
                .WithParsed(Process);
        }

        private class Writer
        {
            private readonly TrackChunk _trackChunk;
            private readonly int _channel;
            private readonly int _index;
            private long _pendingDeltaTime;

            public Writer(int channel, int index)
            {
                _channel = channel;
                _index = index;
                _trackChunk = new TrackChunk();
                _pendingDeltaTime = 0;
            }

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
            public long TotalLength => _trackChunk.GetTimedEvents().LastOrDefault()?.Time ?? 0L + _pendingDeltaTime;

            public void WriteToDisk(long totalLength)
            {
                var midiFile = new MidiFile(_trackChunk);
                var filePath = $"foo_{_channel}_{_index}.mid";

                var tempoMap = midiFile.GetTempoMap();
                var lastEvent = midiFile.GetTimedEvents().Last();
                if (lastEvent.Time < totalLength)
                {
                    _trackChunk.Events.Add(new ControlChangeEvent{DeltaTime = totalLength - lastEvent.Time});
                }

                midiFile.Write(filePath, true, MidiFileFormat.SingleTrack);
                Console.WriteLine($"Wrote to {filePath}, duration {(TimeSpan)midiFile.GetTimedEvents().Last().TimeAs<MetricTimeSpan>(tempoMap)}");
            }

            public void AddDelay(long deltaTime)
            {
                _pendingDeltaTime += deltaTime;
            }
        }

        static void Process(Options options)
        {
            switch (options.Action.ToLowerInvariant())
            {
                case "split":
                    Split(options.Filename);
                    break;

                case "print":
                    Print(options.Filename);
                    break;
            }
        }

        static void Split(string filename)
        {
            // We open the file...
            var f = MidiFile.Read(filename);

            // We count the polyphony
            var channelVoiceCounts = Enumerable.Repeat(0, 100).ToList();
            var maxVoiceCounts = Enumerable.Repeat(0, 100).ToList();
            foreach (var e in f.GetTimedEvents())
            {
                switch (e.Event)
                {
                    case NoteOffEvent n:
                        channelVoiceCounts[n.Channel] -= 1;
                        break;
                    case NoteOnEvent n:
                        channelVoiceCounts[n.Channel] += 1;
                        if (channelVoiceCounts[n.Channel] > maxVoiceCounts[n.Channel])
                        {
                            maxVoiceCounts[n.Channel] = channelVoiceCounts[n.Channel];
                        }
                        break;
                }
            }
            for (var i = 0; i < maxVoiceCounts.Count; i++)
            {
                if (maxVoiceCounts[i] > 0)
                {
                    Console.WriteLine($"Channel {i} has {maxVoiceCounts[i]} voice polyphony");
                }
            }

            // We create all these output files...
            var writers = new Dictionary<int, List<Writer>>();
            for (var channelIndex = 0; channelIndex < maxVoiceCounts.Count; channelIndex++)
            {
                if (maxVoiceCounts[channelIndex] > 0)
                {
                    if (!writers.ContainsKey(channelIndex))
                    {
                        writers.Add(channelIndex, new List<Writer>());
                    }

                    for (int voiceIndex = 0; voiceIndex < maxVoiceCounts[channelIndex]; ++voiceIndex)
                    {
                        var writer = new Writer(channelIndex, voiceIndex);
                        writers[channelIndex].Add(writer);
                    }
                }
            }

            // Then we parse the file and filter note events to different files
            // Note that note off events have to match the note on events
            foreach (var e in f.GetTrackChunks().SelectMany(chunk => chunk.Events))
            {
                if (e is ChannelEvent channelEvent)
                {
                    switch (channelEvent)
                    {
                        case NoteOnEvent n:
                        {
                            bool written = false;
                            foreach (var writer in writers[channelEvent.Channel])
                            {
                                // We write the note on to the first available one, and we write just the time to the rest
                                if (writer.Available && !written)
                                {
                                    writer.Write(n);
                                    written = true;
                                }
                                else
                                {
                                    writer.AddDelay(n.DeltaTime);
                                }
                            }
                            break;
                        }
                        case NoteOffEvent n:
                            foreach (var writer in writers[channelEvent.Channel])
                            {
                                // We send the note off to the one that's playing it, and add delays to the rest
                                if (writer.NoteNumber.Equals(n.NoteNumber))
                                {
                                    writer.Write(n);
                                }
                                else
                                {
                                    writer.AddDelay(n.DeltaTime);
                                }
                            }
                            break;
                        default:
                            // Everything else goes to them all
                            foreach (var writer in writers[channelEvent.Channel])
                            {
                                writer.Write(channelEvent);
                            }
                            break;
                    }
                }
                else
                {
                    // Pass everything else through to all the files.
                    foreach (var writer in writers.Values.SelectMany(x => x))
                    {
                        writer.Write(e);
                    }
                }
            }

            var totalLength = writers.Values.SelectMany(x => x).Max(w => w.TotalLength);

            foreach (var writer in writers.Values.SelectMany(x => x))
            {
                writer.WriteToDisk(totalLength);
            }
        }
        
        static void Print(string filename)
        {
            // We open the file...
            var f = MidiFile.Read(filename);

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
