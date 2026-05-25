namespace BrainX.Core.Models;

public enum KnowledgeCategory
{
    Programming,
    AI_MachineLearning,
    Blockchain_Web3,
    Science,
    Mathematics,
    Engineering,
    Design_Art,
    Music,
    Writing_Literature,
    Business_Finance,
    Health_Medicine,
    Philosophy,
    History,
    Languages,
    DevOps_Cloud,
    Security_Crypto,
    DataScience,
    GameDev,
    Mobile_Development,
    Web_Development,
    Networking,
    Psychology,
    Education,
    Research,
    Other
}

public class ExpertiseScore
{
    public KnowledgeCategory Category { get; set; }
    public double Score { get; set; }
    public int NoteCount { get; set; }
    public long TotalWords { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public double GrowthRate { get; set; }
}
