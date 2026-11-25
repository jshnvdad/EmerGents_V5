namespace EmergencyApp.User.Reliability;

/// The three delivery stages the user sees: Sent -> Received -> Acknowledged by dispatcher.
public enum DeliveryStep { Queued = 0, Sent = 1, Received = 2, Acknowledged = 3 }

public static class DeliveryCalculator
{
    public static DeliveryStep Step(bool serverHasIt, DateTime? sentAt, DateTime? acknowledgedAt, string? status)
    {
        var s = (status ?? "").Trim().ToLowerInvariant();

        // A dispatcher acknowledged it, or it moved past "Received" (works with older servers too).
        if (acknowledgedAt is not null || s is "dispatched" or "arrived" or "resolved") return DeliveryStep.Acknowledged;
        if (serverHasIt) return DeliveryStep.Received;   // the server's reply to the upload is the ACK
        if (sentAt is not null) return DeliveryStep.Sent; // left the phone (SMS) but no server ACK yet
        return DeliveryStep.Queued;
    }
}
