namespace TCJ.Messaging.Sagas.Migration;

/// <summary>Explicitly migrates one persisted Saga state payload schema version to the next supported version.</summary>
public interface ISagaStateMigrator
{
    /// <summary>Source state schema version.</summary>
    int FromVersion { get; }
    /// <summary>Target state schema version.</summary>
    int ToVersion { get; }
    /// <summary>Migrates serialized state without changing Saga identity or correlation.</summary>
    /// <param name="statePayload">Serialized state payload at <see cref="FromVersion"/>.</param>
    /// <returns>Serialized state payload compatible with <see cref="ToVersion"/>.</returns>
    string Migrate(string statePayload);
}
