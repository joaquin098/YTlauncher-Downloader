using System;
using System.IO;
using System.Media;
using System.Threading.Tasks;

namespace YTMusicLauncher.Services
{
    /// <summary>
    /// Lightweight UI Sound Service that generates soft, pleasant synthesized WAV clicks & feedback sounds in memory.
    /// Zero external file dependencies and instant low-latency playback.
    /// </summary>
    public static class SoundService
    {
        private static readonly MemoryStream _clickStream = CreateSoftSineWav(frequency: 700, durationMs: 25, maxVolume: 0.15f);
        private static readonly MemoryStream _actionStream = CreateSoftSineWav(frequency: 950, durationMs: 35, maxVolume: 0.20f);
        private static readonly MemoryStream _cancelStream = CreateSoftSineWav(frequency: 450, durationMs: 40, maxVolume: 0.15f);

        private static readonly SoundPlayer _clickPlayer = new(_clickStream);
        private static readonly SoundPlayer _actionPlayer = new(_actionStream);
        private static readonly SoundPlayer _cancelPlayer = new(_cancelStream);

        static SoundService()
        {
            try
            {
                _clickStream.Position = 0;
                _clickPlayer.Load();

                _actionStream.Position = 0;
                _actionPlayer.Load();

                _cancelStream.Position = 0;
                _cancelPlayer.Load();
            }
            catch { }
        }

        public static void PlayClick()
        {
            Task.Run(() =>
            {
                try
                {
                    _clickStream.Position = 0;
                    _clickPlayer.Play();
                }
                catch { }
            });
        }

        public static void PlayAction()
        {
            Task.Run(() =>
            {
                try
                {
                    _actionStream.Position = 0;
                    _actionPlayer.Play();
                }
                catch { }
            });
        }

        public static void PlayCancel()
        {
            Task.Run(() =>
            {
                try
                {
                    _cancelStream.Position = 0;
                    _cancelPlayer.Play();
                }
                catch { }
            });
        }

        /// <summary>
        /// Synthesizes a soft, clean 16-bit PCM mono WAV stream in memory with exponential envelope decay.
        /// </summary>
        private static MemoryStream CreateSoftSineWav(double frequency, int durationMs, float maxVolume)
        {
            int sampleRate = 44100;
            int numSamples = (sampleRate * durationMs) / 1000;
            short[] samples = new short[numSamples];

            for (int i = 0; i < numSamples; i++)
            {
                double t = (double)i / sampleRate;
                // Exponential decay envelope for a soft tap/pop sound
                double envelope = Math.Exp(-i * 8.0 / numSamples) * maxVolume;
                double sine = Math.Sin(2 * Math.PI * frequency * t);
                samples[i] = (short)(sine * envelope * short.MaxValue);
            }

            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);

            int subchunk2Size = numSamples * 2;
            int chunkSize = 36 + subchunk2Size;

            // RIFF header
            writer.Write(new char[] { 'R', 'I', 'F', 'F' });
            writer.Write(chunkSize);
            writer.Write(new char[] { 'W', 'A', 'V', 'E' });

            // fmt subchunk
            writer.Write(new char[] { 'f', 'm', 't', ' ' });
            writer.Write(16); // Subchunk1Size (16 for PCM)
            writer.Write((short)1); // AudioFormat (1 = PCM)
            writer.Write((short)1); // NumChannels (1 = Mono)
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2); // ByteRate
            writer.Write((short)2); // BlockAlign
            writer.Write((short)16); // BitsPerSample

            // data subchunk
            writer.Write(new char[] { 'd', 'a', 't', 'a' });
            writer.Write(subchunk2Size);

            foreach (var sample in samples)
            {
                writer.Write(sample);
            }

            writer.Flush();
            ms.Position = 0;
            return ms;
        }
    }
}
