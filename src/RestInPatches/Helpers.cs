namespace RestInPatches;

public class DestroyWhenInvisible : MonoBehaviour
{
    private void Start()
    {
        if (!GetComponent<SpriteRenderer>().isVisible)
        {
            Destroy(gameObject);
        }
    }

    private void OnBecameInvisible() => Destroy(gameObject);
}

// Fades a retired footprint out over the Footprint Fade Time setting and removes it. Uses
// unscaled time so it still finishes while the settings menu has the game paused. The
// duration is read once per print, so moving the slider can't brighten one already fading.
public class FadeOutAndDestroy : MonoBehaviour
{
    private const float FallbackDuration = 1f;

    private SpriteRenderer _spr;
    private float _startAlpha;
    private float _duration;
    private float _elapsed;

    private void Start()
    {
        _spr = GetComponent<SpriteRenderer>();
        if (_spr == null)
        {
            Destroy(gameObject);
            return;
        }

        _duration = Plugin.FootprintFadeSeconds.Value;
        if (float.IsNaN(_duration) || float.IsInfinity(_duration))
        {
            _duration = FallbackDuration;
        }

        if (_duration <= 0f)
        {
            Destroy(gameObject);
            return;
        }

        _startAlpha = _spr.color.a;
    }

    private void Update()
    {
        if (_spr == null)
        {
            Destroy(gameObject);
            return;
        }

        _elapsed += Time.unscaledDeltaTime;
        if (_elapsed >= _duration)
        {
            Destroy(gameObject);
            return;
        }

        var color = _spr.color;
        color.a = Mathf.Lerp(_startAlpha, 0f, _elapsed / _duration);
        _spr.color = color;
    }
}
