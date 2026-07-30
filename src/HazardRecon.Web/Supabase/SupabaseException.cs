namespace HazardRecon.Web.Supabase;

public class SupabaseException : Exception
{
    public int StatusCode { get; }

    public SupabaseException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}
