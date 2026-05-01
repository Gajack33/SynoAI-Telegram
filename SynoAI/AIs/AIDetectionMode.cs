namespace SynoAI.AIs
{
    /// <summary>
    /// The AI endpoint family SynoAI should call.
    /// </summary>
    public enum AIDetectionMode
    {
        /// <summary>
        /// Object detection using labels such as person, car, dog.
        /// </summary>
        ObjectDetection,

        /// <summary>
        /// Face recognition using user ids returned by the AI service.
        /// </summary>
        FaceRecognition
    }
}
