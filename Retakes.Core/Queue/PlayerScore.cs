namespace Retakes.Queue;

internal sealed class PlayerScore
{
    public int Score   { get; private set; }
    public int Kills   { get; private set; }
    public int Assists { get; private set; }
    public int Defuses { get; private set; }

    public void AddKill()
    {
        Kills++;
        Score++;
    }

    public void AddAssist()
    {
        Assists++;
    }

    public void AddDefuse()
    {
        Defuses++;
        Score += 2;
    }

    public void Reset()
    {
        Score   = 0;
        Kills   = 0;
        Assists = 0;
        Defuses = 0;
    }
}
