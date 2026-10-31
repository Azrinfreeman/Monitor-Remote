// Steps the screen quality down when frames take too long to reach the phone, and back up slowly when the link is healthy.
// It never goes above the quality the user chose.
sealed class AdaptiveQuality {
    static readonly string[] Levels = ["low", "balanced", "clear"];
    readonly int ceiling;
    int level, fast, slow;
    double average;
    public AdaptiveQuality(string requested) { ceiling = Math.Max(0, Array.IndexOf(Levels, requested)); level = ceiling; }
    public string Current => Levels[level];
    public bool Reduced => level < ceiling;
    // sendTime is how long handing one frame to the socket took; a slow link applies back-pressure and lengthens it.
    public string Observe(TimeSpan sendTime) {
        var ms = sendTime.TotalMilliseconds;
        average = average == 0 ? ms : average * .7 + ms * .3;
        if (average > 90) { slow++; fast = 0; }
        else if (average < 30) { fast++; slow = 0; }
        else { slow = fast = 0; }
        if (slow >= 3 && level > 0) { level--; slow = 0; average = 0; }
        else if (fast >= 60 && level < ceiling) { level++; fast = 0; average = 0; }
        return Current;
    }
}
