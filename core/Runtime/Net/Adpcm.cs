using System;

namespace Tailed.Core.Net
{
    /// <summary>
    /// IMA ADPCM (4 bits per sample) for voice chat: 16 kHz mono speech at 64 kbps, no native codec
    /// needed. Each packet carries the predictor state so packets decode independently (loss-tolerant).
    /// </summary>
    public static class Adpcm
    {
        static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };
        static readonly int[] StepTable =
        {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97,
            107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
            876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871,
            5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623,
            27086, 29794, 32767,
        };

        public struct State { public int Predictor, Index; }

        /// <summary>Header (3 bytes: predictor, index) + samples/2 bytes.</summary>
        public static int EncodedSize(int samples) => 3 + (samples + 1) / 2;

        public static void Encode(ReadOnlySpan<float> pcm, ref State st, ByteWriter w)
        {
            w.I16((short)st.Predictor);
            w.U8((byte)st.Index);
            int pending = -1;
            foreach (float f in pcm)
            {
                int sample = (int)Math.Round(Math.Clamp(f, -1f, 1f) * 32767f);
                int step = StepTable[st.Index];
                int diff = sample - st.Predictor;
                int code = 0;
                if (diff < 0) { code = 8; diff = -diff; }
                int delta = step >> 3;
                if (diff >= step) { code |= 4; diff -= step; delta += step; }
                if (diff >= step >> 1) { code |= 2; diff -= step >> 1; delta += step >> 1; }
                if (diff >= step >> 2) { code |= 1; delta += step >> 2; }
                st.Predictor = Math.Clamp(st.Predictor + ((code & 8) != 0 ? -delta : delta), -32768, 32767);
                st.Index = Math.Clamp(st.Index + IndexTable[code], 0, 88);
                if (pending < 0) pending = code;
                else { w.U8((byte)(pending | (code << 4))); pending = -1; }
            }
            if (pending >= 0) w.U8((byte)pending);
        }

        /// <summary>Decode <paramref name="samples"/> samples from the reader into <paramref name="output"/>.</summary>
        public static void Decode(ByteReader r, int samples, Span<float> output)
        {
            int predictor = r.I16();
            int index = Math.Clamp((int)r.U8(), 0, 88);
            for (int i = 0; i < samples; i += 2)
            {
                byte b = r.U8();
                for (int k = 0; k < 2 && i + k < samples; k++)
                {
                    int code = k == 0 ? b & 0xF : b >> 4;
                    int step = StepTable[index];
                    int delta = step >> 3;
                    if ((code & 4) != 0) delta += step;
                    if ((code & 2) != 0) delta += step >> 1;
                    if ((code & 1) != 0) delta += step >> 2;
                    predictor = Math.Clamp(predictor + ((code & 8) != 0 ? -delta : delta), -32768, 32767);
                    index = Math.Clamp(index + IndexTable[code], 0, 88);
                    output[i + k] = predictor / 32768f;
                }
            }
        }
    }
}
