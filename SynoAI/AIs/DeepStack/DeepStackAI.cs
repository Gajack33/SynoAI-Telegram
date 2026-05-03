using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SynoAI.App;
using SynoAI.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SynoAI.AIs;

namespace SynoAI.AIs.DeepStack
{
    public class DeepStackAI : AI
    {
        public async override Task<IEnumerable<AIPrediction>> Process(ILogger logger, Camera camera, byte[] image)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            string providerName = Config.AI == AIType.CodeProjectAIServer ? "CodeProject.AI" : "DeepStackAI";

            decimal minConfidence = camera.Threshold / 100m;

            string aiPath = Config.AIDetectionMode == AIDetectionMode.FaceRecognition
                ? Config.AIFaceRecognitionPath
                : Config.AIPath;

            logger.LogDebug($"{camera.Name}: {providerName}: POSTing image with minimum confidence of {minConfidence} ({camera.Threshold}%) to {string.Join("/", Config.AIUrl, aiPath)}.");

            try
            {
                Uri uri = GetUri(Config.AIUrl, aiPath);
                int maxAttempts = Config.HttpRetryCount + 1;
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        using MultipartFormDataContent multipartContent = CreateMultipartContent(image, minConfidence);
                        using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromSeconds(Config.AITimeoutSeconds));
                        using HttpResponseMessage response = await Shared.HttpClient.PostAsync(uri, multipartContent, cancellationTokenSource.Token);
                        if (response.IsSuccessStatusCode)
                        {
                            DeepStackResponse deepStackResponse = await GetResponse(logger, camera, providerName, response);
                            if (deepStackResponse?.Success == true)
                            {
                                IEnumerable<AIPrediction> predictions = (deepStackResponse.Predictions ?? Enumerable.Empty<DeepStackPrediction>())
                                    .Where(x => NormaliseConfidenceToFraction(x.Confidence) >= minConfidence)
                                    .Select(x => new AIPrediction()
                                    {
                                        Confidence = NormaliseConfidenceToPercent(x.Confidence),
                                        Label = Config.MapFaceLabel(x.Label),
                                        MaxX = x.MaxX,
                                        MaxY = x.MaxY,
                                        MinX = x.MinX,
                                        MinY = x.MinY
                                    }).ToList();

                                stopwatch.Stop();
                                logger.LogInformation(
                                    "{cameraName}: {providerName}: Processed successfully in {elapsedMs}ms. Inference={inferenceMs}ms Process={processMs}ms RoundTrip={roundTripMs}ms Module={moduleName} Provider={executionProvider}",
                                    camera.Name,
                                    providerName,
                                    stopwatch.ElapsedMilliseconds,
                                    deepStackResponse.InferenceMs,
                                    deepStackResponse.ProcessMs,
                                    deepStackResponse.AnalysisRoundTripMs,
                                    deepStackResponse.ModuleName,
                                    deepStackResponse.ExecutionProvider);
                                return predictions;
                            }

                            string error = deepStackResponse == null
                                ? "Empty or invalid response."
                                : string.IsNullOrWhiteSpace(deepStackResponse.Error) ? deepStackResponse.Message : deepStackResponse.Error;
                            logger.LogWarning($"{camera.Name}: {providerName}: Failed with error '{error ?? "Unknown error"}'.");
                            return null;
                        }

                        string responseBody = await ReadContentAsStringAsync(response.Content, Config.MaxAIResponseBytes);
                        logger.LogWarning($"{camera.Name}: {providerName}: Failed to call API with HTTP status code '{response.StatusCode}'. Response: {responseBody}");
                        if (ShouldRetry(response.StatusCode, attempt, maxAttempts))
                        {
                            await DelayBeforeRetry(logger, camera, providerName, attempt, maxAttempts);
                            continue;
                        }

                        return null;
                    }
                    catch (TaskCanceledException ex) when (attempt < maxAttempts)
                    {
                        logger.LogWarning(ex, "{cameraName}: {providerName}: Request timed out on attempt {attempt} of {maxAttempts}.", camera.Name, providerName, attempt, maxAttempts);
                        await DelayBeforeRetry(logger, camera, providerName, attempt, maxAttempts);
                    }
                    catch (HttpRequestException ex) when (attempt < maxAttempts)
                    {
                        logger.LogWarning(ex, "{cameraName}: {providerName}: Request failed on attempt {attempt} of {maxAttempts}.", camera.Name, providerName, attempt, maxAttempts);
                        await DelayBeforeRetry(logger, camera, providerName, attempt, maxAttempts);
                    }
                }
            }
            catch (TaskCanceledException ex)
            {
                logger.LogError(ex, $"{camera.Name}: {providerName}: Request timed out after {Config.AITimeoutSeconds} seconds.");
            }
            catch (HttpRequestException ex)
            {
                logger.LogError(ex, $"{camera.Name}: {providerName}: Failed to call API.");
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, $"{camera.Name}: {providerName}: Failed to parse API response.");
            }
            catch (InvalidDataException ex)
            {
                logger.LogError(ex, $"{camera.Name}: {providerName}: Response exceeded configured size limits.");
            }
            catch (Exception ex) when (ex is ArgumentException || ex is UriFormatException)
            {
                logger.LogError(ex, $"{camera.Name}: {providerName}: AI URL is invalid.");
            }

            return null;
        }

        private static MultipartFormDataContent CreateMultipartContent(byte[] image, decimal minConfidence)
        {
            StreamContent imageContent = new(new MemoryStream(image));
            imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

            return new MultipartFormDataContent
            {
                { imageContent, "image", "image.jpg" },
                { new StringContent(minConfidence.ToString(CultureInfo.InvariantCulture)), "min_confidence" }
            };
        }

        private static bool ShouldRetry(System.Net.HttpStatusCode statusCode, int attempt, int maxAttempts)
        {
            int status = (int)statusCode;
            return attempt < maxAttempts && (status == 408 || status == 429 || status >= 500);
        }

        private static async Task DelayBeforeRetry(ILogger logger, Camera camera, string providerName, int attempt, int maxAttempts)
        {
            int delayMs = Config.HttpRetryDelayMs * attempt;
            if (delayMs <= 0)
            {
                return;
            }

            logger.LogInformation(
                "{cameraName}: {providerName}: Retrying transient failure after {delayMs}ms ({nextAttempt}/{maxAttempts}).",
                camera.Name,
                providerName,
                delayMs,
                attempt + 1,
                maxAttempts);
            await Task.Delay(delayMs);
        }

        /// <summary>
        /// Builds a <see cref="Uri"/> from the provided base and resource.
        /// </summary>
        /// <param name="basePath"></param>
        /// <param name="resourcePath"></param>
        /// <returns>A <see cref="Uri"/> for the combined base and resource.</returns>
        protected Uri GetUri(string basePath, string resourcePath)
        {
            Uri baseUri = new(basePath);
            return new Uri(baseUri, resourcePath);
        }

        /// <summary>
        /// Fetches the response content and parses it a DeepStack object.
        /// </summary>
        /// <param name="message">The message to parse.</param>
        /// <returns>A usable object.</returns>
        private async Task<DeepStackResponse> GetResponse(ILogger logger, Camera camera, string providerName, HttpResponseMessage message)
        {
            string content = await ReadContentAsStringAsync(message.Content, Config.MaxAIResponseBytes);
            logger.LogDebug($"{camera.Name}: {providerName}: Responded with {content}.");

            return JsonConvert.DeserializeObject<DeepStackResponse>(content);
        }

        private static async Task<string> ReadContentAsStringAsync(HttpContent content, long maxBytes)
        {
            if (maxBytes > 0 && content.Headers.ContentLength.HasValue && content.Headers.ContentLength.Value > maxBytes)
            {
                throw new InvalidDataException($"Response content length {content.Headers.ContentLength.Value} exceeds configured limit {maxBytes}.");
            }

            using Stream stream = await content.ReadAsStreamAsync();
            using MemoryStream output = new();
            byte[] buffer = new byte[81920];
            long totalBytes = 0;
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                totalBytes += bytesRead;
                if (maxBytes > 0 && totalBytes > maxBytes)
                {
                    throw new InvalidDataException($"Response exceeded configured limit {maxBytes}.");
                }

                output.Write(buffer, 0, bytesRead);
            }

            return Encoding.UTF8.GetString(output.ToArray());
        }

        private static decimal NormaliseConfidenceToFraction(decimal confidence)
        {
            return confidence > 1 ? confidence / 100m : confidence;
        }

        private static decimal NormaliseConfidenceToPercent(decimal confidence)
        {
            return confidence > 1 ? confidence : confidence * 100m;
        }
    }
}
