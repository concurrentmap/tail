using System;
using Tailed.Core.Net;
using Xunit;

namespace Tailed.Core.Tests
{
    public class AdpcmTests
    {
        [Fact]
        public void SpeechLikeSignal_RoundTripsWithGoodSnr()
        {
            const int rate = 16000, n = 320; // 20 ms frames
            var st = new Adpcm.State();
            double sig = 0, noise = 0;
            for (int frame = 0; frame < 50; frame++)
            {
                var pcm = new float[n];
                for (int i = 0; i < n; i++)
                {
                    double t = (frame * n + i) / (double)rate;
                    // Voiced-ish: harmonics of a wobbling 140 Hz fundamental with a syllable envelope.
                    double f0 = 140 + 20 * Math.Sin(2 * Math.PI * 3 * t);
                    double env = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 4 * t);
                    pcm[i] = (float)(env * 0.3 * (Math.Sin(2 * Math.PI * f0 * t) + 0.5 * Math.Sin(4 * Math.PI * f0 * t) + 0.25 * Math.Sin(6 * Math.PI * f0 * t)));
                }
                var w = new ByteWriter();
                Adpcm.Encode(pcm, ref st, w);
                Assert.Equal(Adpcm.EncodedSize(n), w.Length);
                var back = new float[n];
                Adpcm.Decode(new ByteReader(w.ToArray()), n, back);
                if (frame < 2) continue; // let the step size adapt
                for (int i = 0; i < n; i++) { sig += pcm[i] * pcm[i]; noise += (pcm[i] - back[i]) * (pcm[i] - back[i]); }
            }
            double snr = 10 * Math.Log10(sig / noise);
            Assert.True(snr > 20, $"SNR {snr:0.0} dB");
        }

        [Fact]
        public void Packets_DecodeIndependently()
        {
            // A lost packet must not corrupt the next: each carries its own predictor state.
            var st = new Adpcm.State();
            var a = new ByteWriter(); var b = new ByteWriter();
            var pcm = new float[160];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)Math.Sin(i * 0.2) * 0.5f;
            Adpcm.Encode(pcm, ref st, a);
            Adpcm.Encode(pcm, ref st, b);
            var outB = new float[160];
            Adpcm.Decode(new ByteReader(b.ToArray()), 160, outB); // decode b without ever seeing a
            double err = 0;
            for (int i = 0; i < 160; i++) err = Math.Max(err, Math.Abs(outB[i] - pcm[i]));
            Assert.True(err < 0.1, $"max error {err:0.000}");
        }
    }
}
