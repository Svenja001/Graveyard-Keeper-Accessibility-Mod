namespace BringOutYerDead;

public static class Helpers
{
    // True once the vanilla donkey has delivered at least once. Replaces an older quest-list check that drifted on some saves.
    internal static bool TutorialDone()
    {
        if (!MainGame.game_started) return false;
        if (MainGame.me?.save?.game_logics == null) return false;
        var donkeyLogic = MainGame.me.save.game_logics.GetLogicByID("donkey");
        return donkeyLogic != null && donkeyLogic._started;
    }

    // Human-readable reason behind the last TutorialDone() result, for debug logs.
    internal static string TutorialDoneReason()
    {
        if (!MainGame.game_started) return "MainGame.game_started==false";
        if (MainGame.me?.save?.game_logics == null) return "save.game_logics==null";
        var donkeyLogic = MainGame.me.save.game_logics.GetLogicByID("donkey");
        if (donkeyLogic == null) return "no LogicData with id='donkey' in save";
        return donkeyLogic._started ? "donkey LogicData._started==true" : "donkey LogicData._started==false (vanilla delivery hasn't fired yet)";
    }

    internal static void Log(string message, bool error = false)
    {
        if (error)
        {
            LogHelper.Error(message);
        }
        else
        {
            LogHelper.Info(message);
        }
    }
}
