using System.Collections;
using System.Reflection;
using UnityEngine;

namespace HKMultiplayer.Util;

/// <summary>
/// Helpers de compatibilidad con Unity 2020.2.2f1 y Hollow Knight 1.5.68.11808.
/// </summary>
internal static class UnityCompat {
    private static readonly FieldInfo HeroInPositionField =
        typeof(HeroController).GetField("heroInPosition", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly MethodInfo InvulnerableMethod = typeof(HeroController).GetMethod(
        "Invulnerable", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(float) }, null);

    /// <summary>En 1.5.68 existe el evento <c>heroInPosition</c> y el flag <c>isHeroInPosition</c> en vez de <c>SendHeroInPosition</c>.</summary>
    public static void SendHeroInPosition(HeroController hero, bool forceDirect) {
        if (hero == null) {
            return;
        }

        hero.isHeroInPosition = true;
        if (HeroInPositionField?.GetValue(hero) is HeroController.HeroInPosition callback) {
            callback.Invoke(forceDirect);
        }
    }

    /// <summary>En 1.5.68 existe la corrutina privada <c>Invulnerable(float)</c> en vez de <c>StartInvulnerable(float)</c>.</summary>
    public static void StartInvulnerable(HeroController hero, float duration) {
        if (hero == null) {
            return;
        }

        if (InvulnerableMethod?.Invoke(hero, new object[] { duration }) is IEnumerator routine) {
            hero.StartCoroutine(routine);
        }
    }

    public static T GetComponentInParentCompat<T>(this Component component, bool includeInactive) where T : Component =>
        component != null ? component.transform.GetComponentInParentCompat<T>(includeInactive) : null;

    public static T GetComponentInParentCompat<T>(this GameObject gameObject, bool includeInactive) where T : Component =>
        gameObject != null ? gameObject.transform.GetComponentInParentCompat<T>(includeInactive) : null;

    private static T GetComponentInParentCompat<T>(this Transform transform, bool includeInactive) where T : Component {
        if (!includeInactive) {
            return transform.GetComponentInParent<T>();
        }

        for (var current = transform; current != null; current = current.parent) {
            var found = current.GetComponent<T>();
            if (found != null) {
                return found;
            }
        }

        return null;
    }
}
