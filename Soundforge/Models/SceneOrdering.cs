namespace Soundforge.Models;

public static class SceneOrdering
{
    public static bool Move(List<Scene> scenes, Scene scene, int offset)
    {
        ArgumentNullException.ThrowIfNull(scenes);
        ArgumentNullException.ThrowIfNull(scene);

        var currentIndex = scenes.IndexOf(scene);
        var targetIndex = currentIndex + offset;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= scenes.Count)
            return false;

        scenes.RemoveAt(currentIndex);
        scenes.Insert(targetIndex, scene);
        return true;
    }

    public static void SortByName(List<Scene> scenes)
    {
        ArgumentNullException.ThrowIfNull(scenes);
        scenes.Sort((left, right) =>
        {
            var caseInsensitive = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            return caseInsensitive != 0
                ? caseInsensitive
                : StringComparer.Ordinal.Compare(left.Name, right.Name);
        });
    }
}
