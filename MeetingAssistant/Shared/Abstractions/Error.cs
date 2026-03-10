namespace MeetingAssistant.Shared.Abstractions
{
    public record Error(string Code,string Description,int Statuscode)
    {
        public static readonly Error None=new(string.Empty,string.Empty,200);
    }
}
