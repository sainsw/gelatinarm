namespace Gelatinarm.Shared.Server
{
    // A Radarr or Sonarr quality profile, on the server Seerr would send the request to
    public class SeerrQualityProfile
    {
        public int ServerId { get; set; }
        public int Id { get; set; }
        public string Name { get; set; }
    }
}
