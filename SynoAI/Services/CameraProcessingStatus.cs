namespace SynoAI.Services
{
    public enum CameraProcessingStatus
    {
        CameraNotFound,
        NoValidObjectDetected,
        ValidObjectDetected,
        AiProcessingFailed,
        ImageAnnotationFailed,
        NotificationFailed,
        ProcessingFailed
    }
}
