namespace ClassInterpreter.Core.Speech;

public static class TranslationModeState
{
    public static bool DirectionSelectorEnabled(bool sessionRunning) => !sessionRunning;

    public static bool ShouldFollowSlides(TranslationDirection direction) => direction.EnableSlideFollowing;
}
