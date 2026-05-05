using Microsoft.Extensions.Logging;
using SynoAI.AIs;
using SynoAI.AIs.DeepStack;
using SynoAI.Models;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SynoAI.Services
{
    public class AIService : IAIService
    {
        private readonly ILogger<AIService> _logger;
        private readonly SynoAI.App.IHttpClient _httpClient;

        public AIService(ILogger<AIService> logger, SynoAI.App.IHttpClient httpClient)
        {
            _logger = logger;
            _httpClient = httpClient;
        }

        public async Task<IEnumerable<AIPrediction>> ProcessAsync(Camera camera, byte[] image)
        {
            AI ai = GetAI();
            byte[] aiImage = PrepareImageForAI(camera, image, out double scaleX, out double scaleY);
            IEnumerable<AIPrediction> predictions = await ai.Process(_logger, camera, aiImage);

            if (predictions == null || (scaleX == 1 && scaleY == 1))
            {
                return predictions;
            }

            return predictions.Select(prediction => new AIPrediction
            {
                Label = prediction.Label,
                Confidence = prediction.Confidence,
                MinX = ScaleCoordinate(prediction.MinX, scaleX),
                MinY = ScaleCoordinate(prediction.MinY, scaleY),
                MaxX = ScaleCoordinate(prediction.MaxX, scaleX),
                MaxY = ScaleCoordinate(prediction.MaxY, scaleY)
            }).ToList();
        }

        public async Task<bool> WarmupAsync()
        {
            if (!Config.AIWarmupEnabled)
            {
                return true;
            }

            byte[] image = CreateWarmupImage();
            Camera camera = new()
            {
                Name = "AI warmup",
                Threshold = 99
            };

            for (int attempt = 1; attempt <= Config.AIWarmupRetries; attempt++)
            {
                _logger.LogInformation("AI warmup attempt {attempt} of {attempts}.", attempt, Config.AIWarmupRetries);

                IEnumerable<AIPrediction> predictions = null;
                try
                {
                    predictions = await ProcessAsync(camera, image);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AI warmup attempt {attempt} failed unexpectedly.", attempt);
                }

                if (predictions != null)
                {
                    _logger.LogInformation("AI warmup completed.");
                    return true;
                }

                if (attempt < Config.AIWarmupRetries && Config.AIWarmupDelayMs > 0)
                {
                    await Task.Delay(Config.AIWarmupDelayMs);
                }
            }

            _logger.LogWarning("AI warmup did not complete after {attempts} attempt(s). SynoAI will continue and retry on motion events.", Config.AIWarmupRetries);
            return false;
        }

        private AI GetAI()
        {
            switch (Config.AI)
            {
                case AIType.DeepStack:
                case AIType.CodeProjectAIServer: // Compatible multipart detection API.
                    return new DeepStackAI(_httpClient);
                default:
                    throw new NotImplementedException(Config.AI.ToString());
            }
        }

        private static byte[] CreateWarmupImage()
        {
            using SKBitmap bitmap = new(64, 64);
            using SKCanvas canvas = new(bitmap);
            canvas.Clear(SKColors.Black);

            using SKImage image = SKImage.FromBitmap(bitmap);
            using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, 75);
            return data.ToArray();
        }

        private byte[] PrepareImageForAI(Camera camera, byte[] image, out double scaleX, out double scaleY)
        {
            scaleX = 1;
            scaleY = 1;

            if (Config.AIMaxImageWidth <= 0)
            {
                return image;
            }

            if (!SnapshotManager.IsSnapshotSizeAllowed(image))
            {
                _logger.LogWarning("{cameraName}: Snapshot exceeds configured size limit. Skipping AI resize.", camera.Name);
                return image;
            }

            using SKBitmap original = DecodeBitmap(image);
            if (original == null || original.Width <= Config.AIMaxImageWidth)
            {
                return image;
            }

            int resizedWidth = Config.AIMaxImageWidth;
            int resizedHeight = Math.Max(1, (int)Math.Round(original.Height * (resizedWidth / (double)original.Width)));
            SKImageInfo imageInfo = new(resizedWidth, resizedHeight, original.ColorType, original.AlphaType, original.ColorSpace);

            SKSamplingOptions sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);
            using SKBitmap resized = original.Resize(imageInfo, sampling);
            if (resized == null)
            {
                _logger.LogWarning("{cameraName}: Failed to resize image for AI. Sending original image.", camera.Name);
                return image;
            }

            using SKImage resizedImage = SKImage.FromBitmap(resized);
            using SKData data = resizedImage.Encode(SKEncodedImageFormat.Jpeg, Config.AIJpegQuality);

            scaleX = original.Width / (double)resized.Width;
            scaleY = original.Height / (double)resized.Height;
            _logger.LogInformation(
                "{cameraName}: Resized image for AI from {originalWidth}x{originalHeight} to {resizedWidth}x{resizedHeight}.",
                camera.Name,
                original.Width,
                original.Height,
                resized.Width,
                resized.Height);

            return data.ToArray();
        }

        private static int ScaleCoordinate(int coordinate, double scale)
        {
            return (int)Math.Round(coordinate * scale, MidpointRounding.AwayFromZero);
        }

        private static SKBitmap DecodeBitmap(byte[] image)
        {
            try
            {
                return SKBitmap.Decode(image);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
