namespace Gelatinarm.Search
{
    public static class SearchConstants
    {
        /// <summary>
        ///     Timeout for search operations to provide quick feedback
        /// </summary>
        public const int SearchTimeoutSeconds = 4;

        /// <summary>
        ///     How often the request page asks Seerr again while a title is processing or
        ///     downloading, for the progress line; Seerr itself refreshes its queue every few seconds
        /// </summary>
        public const int RequestProgressRefreshSeconds = 10;
    }
}
