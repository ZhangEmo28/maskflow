public sealed record MaskFlowLoadResult(MaskFlowState? State, bool RequiresFullWrite);

public interface IMaskFlowRepository
{
    Task EnsureSchemaAsync();
    Task<MaskFlowLoadResult> LoadAsync();

    // previous == null: write the whole state (e.g. migrating a legacy JSON state).
    Task SaveChangesAsync(MaskFlowState? previous, MaskFlowState current);
}
