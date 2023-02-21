﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using K4os.Hash.xxHash;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.Render {
    public class RenderNote {
        public readonly string lyric;
        public readonly int tone;

        public readonly int position;
        public readonly int duration;
        public readonly int end;

        public readonly double positionMs;
        public readonly double durationMs;
        public readonly double endMs;

        public RenderNote(UProject project, UPart part, UNote note, int phrasePosition) {
            lyric = note.lyric;
            tone = note.tone;

            position = part.position + note.position - phrasePosition;
            duration = note.duration;
            end = position + duration;

            positionMs = project.timeAxis.TickPosToMsPos(part.position + note.position);
            endMs = project.timeAxis.TickPosToMsPos(part.position + note.End);
            durationMs = endMs - positionMs;
        }
    }

    public class RenderPhone {
        // Relative ticks
        public readonly int position;
        public readonly int duration;
        public readonly int end;
        public readonly int leading;

        // Absolute milliseconds
        public readonly double positionMs;
        public readonly double durationMs;
        public readonly double endMs;
        public readonly double leadingMs;

        public readonly string phoneme;
        public readonly int tone;
        public readonly int noteIndex;
        public readonly double tempo;

        // classic args
        public readonly double preutterMs;
        public readonly double overlapMs;
        public readonly double durCorrectionMs;
        public readonly string resampler;
        public readonly Tuple<string, int?>[] flags;
        public readonly string suffix;
        public readonly float volume;
        public readonly float velocity;
        public readonly float modulation;
        public readonly Vector2[] envelope;

        public readonly UOto oto;
        public readonly ulong hash;

        internal RenderPhone(UProject project, UTrack track, UVoicePart part, UNote note, UPhoneme phoneme, int phrasePosition) {
            position = part.position + phoneme.position - phrasePosition;
            duration = phoneme.Duration;
            end = position + duration;
            positionMs = phoneme.PositionMs;
            durationMs = phoneme.DurationMs;
            endMs = phoneme.EndMs;
            leadingMs = phoneme.preutter;
            leading = Math.Max(0, project.timeAxis.TicksBetweenMsPos(positionMs - leadingMs, positionMs));

            this.phoneme = phoneme.phoneme;
            tone = note.tone;
            tempo = project.timeAxis.GetBpmAtTick(part.position + phoneme.position);
            preutterMs = phoneme.preutter;
            overlapMs = phoneme.overlap;
            durCorrectionMs = phoneme.preutter - phoneme.tailIntrude + phoneme.tailOverlap;

            resampler = track.RendererSettings.resampler;
            int eng = (int)phoneme.GetExpression(project, track, Format.Ustx.ENG).Item1;
            if (project.expressions.TryGetValue(Format.Ustx.ENG, out var descriptor)
                && eng >= 0 && eng < descriptor.options.Length
                && !string.IsNullOrEmpty(descriptor.options[eng])) {
                resampler = descriptor.options[eng];
            }
            flags = phoneme.GetResamplerFlags(project, track);
            string voiceColor = phoneme.GetVoiceColor(project, track);
            suffix = track.Singer.Subbanks.FirstOrDefault(
                subbank => subbank.Color == voiceColor)?.Suffix;
            volume = phoneme.GetExpression(project, track, Format.Ustx.VOL).Item1 * 0.01f;
            velocity = phoneme.GetExpression(project, track, Format.Ustx.VEL).Item1 * 0.01f;
            modulation = phoneme.GetExpression(project, track, Format.Ustx.MOD).Item1 * 0.01f;
            leadingMs = phoneme.preutter;
            envelope = phoneme.envelope.data.ToArray();

            oto = phoneme.oto;
            hash = Hash();
        }

        private ulong Hash() {
            using (var stream = new MemoryStream()) {
                using (var writer = new BinaryWriter(stream)) {
                    writer.Write(duration);
                    writer.Write(phoneme ?? string.Empty);
                    writer.Write(tone);

                    writer.Write(resampler ?? string.Empty);
                    foreach (var flag in flags) {
                        writer.Write(flag.Item1);
                        if (flag.Item2.HasValue) {
                            writer.Write(flag.Item2.Value);
                        }
                    }
                    writer.Write(suffix ?? string.Empty);
                    writer.Write(volume);
                    writer.Write(velocity);
                    writer.Write(modulation);
                    writer.Write(leadingMs);
                    foreach (var point in envelope) {
                        writer.Write(point.X);
                        writer.Write(point.Y);
                    }
                    return XXH64.DigestOf(stream.ToArray());
                }
            }
        }
    }

    public class RenderPhrase {
        public readonly USinger singer;
        public readonly TimeAxis timeAxis;

        public readonly int position;
        public readonly int duration;
        public readonly int end;
        public readonly int leading;

        public readonly double positionMs;
        public readonly double durationMs;
        public readonly double endMs;
        public readonly double leadingMs;

        public readonly RenderNote[] notes;
        public readonly RenderPhone[] phones;

        public readonly float[] pitches;
        public readonly float[] pitchesBeforeDeviation;
        public readonly float[] dynamics;
        public readonly float[] gender;
        public readonly float[] breathiness;
        public readonly float[] toneShift;
        public readonly float[] tension;
        public readonly float[] voicing;
        public readonly Tuple<string, float[]>[] curves;//custom curves defined by renderer
        public readonly ulong preEffectHash;
        public readonly ulong hash;

        internal readonly IRenderer renderer;
        public readonly string wavtool;

        internal RenderPhrase(UProject project, UTrack track, UVoicePart part, IEnumerable<UPhoneme> phonemes) {
            var uNotes = new List<UNote>();
            uNotes.Add(phonemes.First().Parent);
            var endNote = phonemes.Last().Parent;
            while (endNote.Next != null && endNote.Next.Extends != null) {
                endNote = endNote.Next;
            }
            while (uNotes.Last() != endNote) {
                uNotes.Add(uNotes.Last().Next);
            }
            var tail = uNotes.Last();
            var next = tail.Next;
            while (next != null && next.Extends == tail) {
                uNotes.Add(next);
                next = next.Next;
            }

            singer = track.Singer;
            renderer = track.RendererSettings.Renderer;
            wavtool = track.RendererSettings.wavtool;
            timeAxis = project.timeAxis.Clone();

            position = part.position + phonemes.First().position;
            end = part.position + phonemes.Last().End;
            duration = end - position;

            notes = uNotes
                .Select(n => new RenderNote(project, part, n, position))
                .ToArray();
            phones = phonemes
                .Select(p => new RenderPhone(project, track, part, p.Parent, p, position))
                .ToArray();

            leading = phones.First().leading;

            positionMs = phones.First().positionMs;
            endMs = phones.Last().endMs;
            durationMs = endMs - positionMs;
            leadingMs = phones.First().leadingMs;

            const int pitchInterval = 5;
            int pitchStart = position - part.position - leading;
            pitches = new float[(end - part.position - pitchStart) / pitchInterval + 1];
            int index = 0;
            foreach (var note in uNotes) {
                while (pitchStart + index * pitchInterval < note.End && index < pitches.Length) {
                    pitches[index] = note.tone * 100;
                    index++;
                }
            }
            index = Math.Max(1, index);
            while (index < pitches.Length) {
                pitches[index] = pitches[index - 1];
                index++;
            }
            foreach (var note in uNotes) {
                if (note.vibrato.length <= 0) {
                    continue;
                }
                int startIndex = Math.Max(0, (int)Math.Ceiling((float)(note.position - pitchStart) / pitchInterval));
                int endIndex = Math.Min(pitches.Length, (note.End - pitchStart) / pitchInterval);
                // Use tempo at note start to calculate vibrato period.
                float nPeriod = (float)(note.vibrato.period / note.DurationMs);
                for (int i = startIndex; i < endIndex; ++i) {
                    float nPos = (float)(pitchStart + i * pitchInterval - note.position) / note.duration;
                    var point = note.vibrato.Evaluate(nPos, nPeriod, note);
                    pitches[i] = point.Y * 100;
                }
            }
            foreach (var note in uNotes) {
                var pitchPoints = note.pitch.data
                    .Select(point => {
                        double nodePosMs = timeAxis.TickPosToMsPos(part.position + note.position);
                        return new PitchPoint(
                               timeAxis.MsPosToTickPos(nodePosMs + point.X) - part.position,
                               point.Y * 10 + note.tone * 100,
                               point.shape);
                    })
                    .ToList();
                if (pitchPoints.Count == 0) {
                    pitchPoints.Add(new PitchPoint(note.position, note.tone * 100));
                    pitchPoints.Add(new PitchPoint(note.End, note.tone * 100));
                }
                if (note == uNotes.First() && pitchPoints[0].X > pitchStart) {
                    pitchPoints.Insert(0, new PitchPoint(pitchStart, pitchPoints[0].Y));
                } else if (pitchPoints[0].X > note.position) {
                    pitchPoints.Insert(0, new PitchPoint(note.position, pitchPoints[0].Y));
                }
                if (pitchPoints.Last().X < note.End) {
                    pitchPoints.Add(new PitchPoint(note.End, pitchPoints.Last().Y));
                }
                PitchPoint lastPoint = pitchPoints[0];
                index = Math.Max(0, (int)((lastPoint.X - pitchStart) / pitchInterval));
                foreach (var point in pitchPoints.Skip(1)) {
                    int x = pitchStart + index * pitchInterval;
                    while (x < point.X && index < pitches.Length) {
                        float pitch = (float)MusicMath.InterpolateShape(lastPoint.X, point.X, lastPoint.Y, point.Y, x, lastPoint.shape);
                        float basePitch = note.Prev != null && x < note.Prev.End
                            ? note.Prev.tone * 100
                            : note.tone * 100;
                        pitches[index] += pitch - basePitch;
                        index++;
                        x += pitchInterval;
                    }
                    lastPoint = point;
                }
            }

            pitchesBeforeDeviation = pitches.ToArray();
            var pitchCurve = part.curves.FirstOrDefault(c => c.abbr == Format.Ustx.PITD);
            if (pitchCurve != null && !pitchCurve.IsEmpty) {
                for (int i = 0; i < pitches.Length; ++i) {
                    pitches[i] += pitchCurve.Sample(pitchStart + i * pitchInterval);
                }
            }

            var curves = new List<Tuple<string, float[]>>();

            foreach(var descriptor in project.expressions.Values) {
                if(descriptor.type != UExpressionType.Curve) {
                    continue;
                }
                var curve = part.curves.FirstOrDefault(c => c.abbr == descriptor.abbr);
                bool isSupported = renderer.SupportsExpression(descriptor);
                if (!isSupported) {
                    continue;
                }
                if (curve == null) {
                    curve = new UCurve(descriptor);
                }
                Func<float, UCurve, float> convert = ((x, _) => x);
                if (curve.abbr == Format.Ustx.DYN) {
                    convert = ((x, c) => x == c.descriptor.min ? 0 : (float)MusicMath.DecibelToLinear(x * 0.1));
                }
                var curveSampled = SampleCurve(curve, pitchStart, pitches.Length, convert);
                switch (curve.abbr) {
                    case Format.Ustx.PITD: break;
                    case Format.Ustx.DYN : dynamics = curveSampled; break;
                    case Format.Ustx.SHFC: toneShift = curveSampled; break;
                    case Format.Ustx.GENC: gender = curveSampled; break;
                    case Format.Ustx.TENC: tension = curveSampled; break;
                    case Format.Ustx.BREC: breathiness = curveSampled; break;
                    case Format.Ustx.VOIC: voicing = curveSampled; break;
                    default:
                        curves.Add(Tuple.Create(curve.abbr,curveSampled));
                        break;
                }
            }
            this.curves = curves.ToArray();
            preEffectHash = Hash(false);
            hash = Hash(true);
        }

        private static float[] SampleCurve(UCurve curve, int start, int length, Func<float, UCurve, float> convert) {
            const int interval = 5;
            var result = new float[length];
            for (int i = 0; i < length; ++i) {
                result[i] = convert(curve.Sample(start + i * interval), curve);
            }
            return result;
        }

        private static float[] SampleCurve(UVoicePart part, string abbr, int start, int length, Func<float, UCurve, float> convert) {
            var curve = part.curves.FirstOrDefault(c => c.abbr == abbr);
            if (curve == null) {
                return null;
            }
            return SampleCurve(curve, start, length, convert);
        }

        private ulong Hash(bool postEffect) {
            using (var stream = new MemoryStream()) {
                using (var writer = new BinaryWriter(stream)) {
                    writer.Write(singer.Id);
                    writer.Write(renderer?.ToString() ?? "");
                    writer.Write(wavtool ?? "");
                    writer.Write(timeAxis.Timestamp);
                    foreach (var phone in phones) {
                        writer.Write(phone.hash);
                    }
                    if (postEffect) {
                        foreach (var array in new float[][] { pitches, dynamics, gender, breathiness, toneShift, tension, voicing }) {
                            if (array == null) {
                                writer.Write("null");
                            } else {
                                foreach (var v in array) {
                                    writer.Write(v);
                                }
                            }
                        }
                        foreach(var curve in curves) {
                            writer.Write(curve.Item1);
                            foreach(var v in curve.Item2) {
                                writer.Write(v);
                            }
                        }
                    }
                    return XXH64.DigestOf(stream.ToArray());
                }
            }
        }

        public static List<RenderPhrase> FromPart(UProject project, UTrack track, UVoicePart part) {
            var phrases = new List<RenderPhrase>();
            var phonemes = part.phonemes
                .Where(phoneme => !phoneme.Error)
                .ToList();
            if (phonemes.Count == 0) {
                return phrases;
            }
            var phrasePhonemes = new List<UPhoneme>() { phonemes[0] };
            for (int i = 1; i < phonemes.Count; ++i) {
                if (phonemes[i - 1].End != phonemes[i].position) {
                    phrases.Add(new RenderPhrase(project, track, part, phrasePhonemes));
                    phrasePhonemes.Clear();
                }
                phrasePhonemes.Add(phonemes[i]);
            }
            if (phrasePhonemes.Count > 0) {
                phrases.Add(new RenderPhrase(project, track, part, phrasePhonemes));
                phrasePhonemes.Clear();
            }
            return phrases;
        }
    }
}
