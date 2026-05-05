using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using SynoAI.Models;
using SynoAI.Extensions;

namespace SynoAI.Services
{

    public class SnapshotManager
    {

        /// <summary>
        /// Dresses the source image by adding the boundary boxes and saves the file locally.
        /// </summary>
        /// <param name="camera">The camera the image came from.</param>
        /// <param name="snapshot">The image data.</param>
        /// <param name="predictions">The list of predictions with the right size (but may or may not be the types configured as interest for this camera).</param>
        /// <param name="validPredictions">The list of predictions with the right size and matching the type of objects of interest for this camera.</param>
        public static ProcessedImage DressImage(Camera camera, byte[] snapshot, IEnumerable<AIPrediction> predictions, IEnumerable<AIPrediction> validPredictions, ILogger logger)
        {
            if (!IsSnapshotSizeAllowed(snapshot))
            {
                logger.LogWarning($"{camera.Name}: Snapshot exceeds configured size limit.");
                return null;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            List<AIPrediction> predictionList = predictions as List<AIPrediction> ?? predictions.ToList();
            List<AIPrediction> validPredictionList = validPredictions as List<AIPrediction> ?? validPredictions.ToList();

            // Load the bitmap
            using SKBitmap image = DecodeBitmap(snapshot);
            if (image == null)
            {
                logger.LogWarning($"{camera.Name}: Failed to decode snapshot for annotation.");
                return null;
            }

            bool hadValidPredictions = validPredictionList.Count > 0;
            predictionList = NormalizePredictions(camera, image.Width, image.Height, predictionList, logger);
            validPredictionList = NormalizePredictions(camera, image.Width, image.Height, validPredictionList, logger);
            if (hadValidPredictions && validPredictionList.Count == 0)
            {
                logger.LogWarning($"{camera.Name}: Valid detections were returned, but none had usable image coordinates.");
                return null;
            }

            // Draw the exclusion zones if enabled
            if (Config.DrawExclusions && camera.Exclusions != null)
            {
                logger.LogInformation($"{camera.Name}: Drawing exclusion zones.");

                using (SKCanvas canvas = new SKCanvas(image))
                {
                    // Draw the zone
                    foreach (Zone zone in camera.Exclusions)
                    {
                        SKRect rectangle = SKRect.Create(zone.Start.X, zone.Start.Y, zone.End.X - zone.Start.X, zone.End.Y - zone.Start.Y);
                        using SKPaint paint = new SKPaint
                        {
                            Style = SKPaintStyle.Stroke,
                            Color = GetColour(Config.ExclusionBoxColor),
                            StrokeWidth = Config.StrokeWidth
                        };
                        canvas.DrawRect(rectangle, paint);
                    }
                }
            }

            // Don't process the drawing if the drawing mode is off
            if (Config.DrawMode == DrawMode.Off)
            {
                logger.LogInformation($"{camera.Name}: Draw mode is Off. Skipping image boundaries.");
            }
            else
            {
                // Draw the predictions
                logger.LogInformation($"{camera.Name}: Dressing image with boundaries.");
                using (SKCanvas canvas = new SKCanvas(image))
                {
                    int counter = 1; //used for assigning a reference number on each prediction if AlternativeLabelling is true

                    foreach (AIPrediction prediction in Config.DrawMode == DrawMode.All ? predictionList : validPredictionList)
                    {
                        // Draw the box
                        SKRect rectangle = SKRect.Create(prediction.MinX, prediction.MinY, prediction.SizeX, prediction.SizeY);
                        using SKPaint strokePaint = new SKPaint
                        {
                            Style = SKPaintStyle.Stroke,
                            Color = GetColour(Config.BoxColor),
                            StrokeWidth = Config.StrokeWidth
                        };
                        canvas.DrawRect(rectangle, strokePaint);

                        // Label creation, either classic label or alternative labelling (and only if there is more than one object)
                        string label = String.Empty;
                        if (Config.AlternativeLabelling && Config.DrawMode == DrawMode.Matches)
                        {
                            // On alternatie labelling, just place a reference number and only if there is more than one object
                            if (validPredictionList.Count > 1)
                            {
                                label = counter.ToString();
                                counter++;
                            }
                        }
                        else
                        {
                            decimal confidence = Math.Round(prediction.Confidence, 0, MidpointRounding.AwayFromZero);
                            label = $"{prediction.Label.FirstCharToUpper()} {confidence}%";
                        }

                        // Label positioning
                        int x = prediction.MinX + Config.TextOffsetX;
                        int y = prediction.MinY + Config.FontSize + Config.TextOffsetY; // FontSize is added as text is drawn above the bottom co-ordinate

                        // Consider below box placement
                        if (Config.LabelBelowBox)
                        {
                            y += prediction.SizeY;
                        }

                        // Draw background box for the text if required
                        using SKTypeface typeface = SKTypeface.FromFamilyName(Config.Font);
                        using SKFont font = new(typeface, Config.FontSize);
                        using SKPaint paint = new SKPaint
                        {
                            IsAntialias = true,
                            Color = GetColour(Config.FontColor)
                        };

                        string textBoxColor = Config.TextBoxColor;
                        if (!string.IsNullOrWhiteSpace(textBoxColor) && !textBoxColor.Equals(SKColors.Transparent.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            float textWidth = font.MeasureText(label);
                            float textBoxWidth = textWidth + (Config.TextOffsetX * 2);
                            float textBoxHeight = Config.FontSize + (Config.TextOffsetY * 2);

                            float textBoxX = prediction.MinX + Config.StrokeWidth;
                            float textBoxY = prediction.MinY + Config.TextOffsetY;
                            if (Config.LabelBelowBox)
                            {
                                textBoxY += prediction.SizeY;
                            }

                            SKRect textRectangle = SKRect.Create(textBoxX, textBoxY, textBoxWidth, textBoxHeight);
                            using SKPaint textBgPaint = new SKPaint
                            {
                                Style = SKPaintStyle.StrokeAndFill,
                                Color = GetColour(textBoxColor),
                                StrokeWidth = Config.StrokeWidth
                            };
                            canvas.DrawRect(textRectangle, textBgPaint);
                        }

                        // Draw the text
                        canvas.DrawText(label, x, y, SKTextAlign.Left, font, paint);
                    }
                }
            }

            stopwatch.Stop();
            logger.LogInformation($"{camera.Name}: Finished dressing image boundaries ({stopwatch.ElapsedMilliseconds}ms).");

            // Save the image, including the amount of valid predictions as suffix.
            string filePath = SaveImage(logger, camera, image, validPredictionList.Count.ToString());
            string relativePath = CaptureFileStore.GetRelativePathFromCameraRoot(camera.Name, filePath);
            return new ProcessedImage(filePath, relativePath);
        }


        /// <summary>
        /// Saves the original unprocessed image from the provided byte array to the camera's capture directory.
        /// </summary>
        /// <param name="camera">The camera to save the image for.</param>
        /// <param name="snapshot">The image to save.</param>
        public static string SaveOriginalImage(ILogger logger, Camera camera, byte[] snapshot)
        {
            if (!IsSnapshotSizeAllowed(snapshot))
            {
                logger.LogWarning($"{camera.Name}: Original snapshot exceeds configured size limit and was not saved.");
                return null;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            string filePath = CreateCaptureFilePath(logger, camera, "Original");
            logger.LogInformation($"{camera}: Saving original image to '{filePath}'.");

            File.WriteAllBytes(filePath, snapshot);

            stopwatch.Stop();
            logger.LogInformation($"{camera}: Original image saved to '{filePath}' ({stopwatch.ElapsedMilliseconds}ms).");
            return filePath;
        }


        /// <summary>
        /// Saves the image to the camera's capture directory.
        /// </summary>
        /// <param name="camera">The camera to save the image for.</param>
        /// <param name="image">The image to save.</param>
        private static string SaveImage(ILogger logger, Camera camera, SKBitmap image, string suffix = null)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            string filePath = CreateCaptureFilePath(logger, camera, suffix);
            logger.LogInformation($"{camera}: Saving image to '{filePath}'.");

            using (FileStream saveStream = new FileStream(filePath, FileMode.CreateNew))
            {
                bool saved = image.Encode(saveStream, SKEncodedImageFormat.Jpeg, Config.OutputJpegQuality);
                stopwatch.Stop();

                if (saved)
                {
                    logger.LogInformation($"{camera}: Image saved to '{filePath}' ({stopwatch.ElapsedMilliseconds}ms).");
                }
                else
                {
                    logger.LogInformation($"{camera}: Failed to save image to '{filePath}' ({stopwatch.ElapsedMilliseconds}ms).");
                }
            }
            return filePath;
        }

        private static string CreateCaptureFilePath(ILogger logger, Camera camera, string suffix = null)
        {
            string directory = Path.Combine(Constants.DIRECTORY_CAPTURES, BuildCaptureDirectory(camera));

            if (!Directory.Exists(directory))
            {
                logger.LogInformation($"{camera}: Creating directory '{directory}'.");
                Directory.CreateDirectory(directory);
            }

            string fileName = String.Empty;
            string uniqueSuffix = Guid.NewGuid().ToString("N").Substring(0, 8);

            if (Config.AlternativeLabelling)
            {
                fileName = $"{DateTime.Now:yyyy_MM_dd_HH_mm_ss_FFF}_{uniqueSuffix}";
                if (!string.IsNullOrWhiteSpace(suffix))
                {
                    fileName += "-" + suffix;
                }
                fileName += ".jpg";
            }
            else
            {
                //Standard file naming
                fileName = $"{CaptureFileStore.ToSafePathSegment(camera.Name)}_{DateTime.Now:yyyy_MM_dd_HH_mm_ss_FFF}_{uniqueSuffix}";
                if (!string.IsNullOrWhiteSpace(suffix))
                {
                    fileName += "_" + suffix;
                }
                fileName += ".jpeg";
            }

            string filePath = Path.Combine(directory, fileName);
            return filePath;
        }

        private static string BuildCaptureDirectory(Camera camera)
        {
            DateTime now = DateTime.Now;
            string pattern = string.IsNullOrWhiteSpace(Config.CapturePathPattern) ? "{camera}" : Config.CapturePathPattern;
            string expanded = pattern
                .Replace("{camera}", CaptureFileStore.ToSafePathSegment(camera.Name), StringComparison.OrdinalIgnoreCase)
                .Replace("{yyyy}", now.ToString("yyyy"), StringComparison.OrdinalIgnoreCase)
                .Replace("{MM}", now.ToString("MM"), StringComparison.OrdinalIgnoreCase)
                .Replace("{dd}", now.ToString("dd"), StringComparison.OrdinalIgnoreCase);

            string[] segments = expanded
                .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => CaptureFileStore.ToSafePathSegment(x))
                .ToArray();

            return segments.Length == 0
                ? CaptureFileStore.ToSafePathSegment(camera.Name)
                : Path.Combine(segments);
        }

        private static SKBitmap DecodeBitmap(byte[] snapshot)
        {
            try
            {
                return SKBitmap.Decode(snapshot);
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

        internal static bool IsSnapshotSizeAllowed(byte[] snapshot)
        {
            return snapshot != null && (Config.MaxSnapshotBytes <= 0 || snapshot.Length <= Config.MaxSnapshotBytes);
        }

        internal static List<AIPrediction> NormalizePredictions(
            Camera camera,
            int imageWidth,
            int imageHeight,
            IEnumerable<AIPrediction> predictions,
            ILogger logger)
        {
            List<AIPrediction> normalizedPredictions = new();
            if (imageWidth <= 0 || imageHeight <= 0)
            {
                logger.LogWarning($"{camera.Name}: Cannot normalize predictions because image dimensions are invalid.");
                return normalizedPredictions;
            }

            foreach (AIPrediction prediction in predictions ?? Enumerable.Empty<AIPrediction>())
            {
                if (prediction == null ||
                    prediction.MaxX <= prediction.MinX ||
                    prediction.MaxY <= prediction.MinY)
                {
                    logger.LogWarning($"{camera.Name}: Ignoring prediction with invalid coordinates.");
                    continue;
                }

                int minX = Math.Clamp(prediction.MinX, 0, imageWidth);
                int minY = Math.Clamp(prediction.MinY, 0, imageHeight);
                int maxX = Math.Clamp(prediction.MaxX, 0, imageWidth);
                int maxY = Math.Clamp(prediction.MaxY, 0, imageHeight);

                if (maxX <= minX || maxY <= minY)
                {
                    logger.LogWarning($"{camera.Name}: Ignoring prediction outside image bounds.");
                    continue;
                }

                normalizedPredictions.Add(new AIPrediction
                {
                    Label = prediction.Label,
                    Confidence = prediction.Confidence,
                    MinX = minX,
                    MinY = minY,
                    MaxX = maxX,
                    MaxY = maxY
                });
            }

            return normalizedPredictions;
        }


        /// <summary>
        /// Parses the provided colour name into an SKColor.
        /// </summary>
        /// <param name="colour">The string to parse.</param>
        private static SKColor GetColour(string hex)
        {
            if (!SKColor.TryParse(hex, out SKColor colour))
            {
                return SKColors.Red;
            }
            return colour;
        }
    }
}
