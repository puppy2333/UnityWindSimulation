using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using static UnityEngine.Rendering.DebugUI;

public class VisualizerSlider
{
    public Slider flowSlider;

    FluidSimConfig cf;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    public VisualizerSlider(FluidSimConfig config, Canvas canvas, float initHandlePos, Vector2 anchorMin, Vector2 anchorMax, Vector2 sliderPos, Vector2 sliderSize, Slider.Direction sliderDir)
    {
        cf = config;

        GameObject sliderObject = new GameObject("FlowSlider", typeof(Slider));
        sliderObject.transform.SetParent(canvas.transform, false);

        flowSlider = sliderObject.GetComponent<Slider>();
        flowSlider.minValue = 0;
        flowSlider.maxValue = 1;
        flowSlider.value = initHandlePos;
        flowSlider.direction = sliderDir;

        RectTransform rectTransform = sliderObject.GetComponent<RectTransform>();
        rectTransform.anchorMin = anchorMin;
        rectTransform.anchorMax = anchorMax;
        rectTransform.anchoredPosition = sliderPos;
        rectTransform.sizeDelta = sliderSize;

        // Background
        GameObject background = new GameObject("Background", typeof(Image));
        background.transform.SetParent(sliderObject.transform, false);
        Image backgroundImage = background.GetComponent<Image>();

        backgroundImage.color = new Color(0.2f, 0.2f, 0.2f);
        RectTransform backgroundRect = background.GetComponent<RectTransform>();
        backgroundRect.anchorMin = new Vector2(0, 0);
        backgroundRect.anchorMax = new Vector2(1, 1);
        backgroundRect.sizeDelta = Vector2.zero;

        // Fill area
        GameObject fillArea = new GameObject("Fill Area", typeof(RectTransform));
        fillArea.transform.SetParent(sliderObject.transform, false);
        RectTransform fillAreaRect = fillArea.GetComponent<RectTransform>();
        fillAreaRect.anchorMin = new Vector2(0, 0);
        fillAreaRect.anchorMax = new Vector2(1, 1);
        fillAreaRect.sizeDelta = new Vector2(0, 0);

        GameObject fill = new GameObject("Fill", typeof(Image));
        fill.transform.SetParent(fillArea.transform, false);
        Image fillImage = fill.GetComponent<Image>();
        // Set fill color
        fillImage.color = new Color(0.5f, 0.8f, 1.0f);
        RectTransform fillRect = fill.GetComponent<RectTransform>();
        fillRect.anchorMin = new Vector2(0, 0);
        fillRect.anchorMax = new Vector2(1, 1);
        fillRect.sizeDelta = Vector2.zero;
        flowSlider.fillRect = fillRect;

        // Handle
        GameObject handleArea = new GameObject("Handle Slider Area", typeof(RectTransform));
        handleArea.transform.SetParent(sliderObject.transform, false);
        RectTransform handleAreaRect = handleArea.GetComponent<RectTransform>();
        handleAreaRect.anchorMin = new Vector2(0, 0);
        handleAreaRect.anchorMax = new Vector2(1, 1);
        handleAreaRect.sizeDelta = new Vector2(-20, 0); // Save sapce for the handle

        GameObject handle = new GameObject("Handle", typeof(Image));
        handle.transform.SetParent(handleArea.transform, false);
        Image handleImage = handle.GetComponent<Image>();
        // Set handle color
        handleImage.color = Color.white;
        RectTransform handleRect = handle.GetComponent<RectTransform>();
        handleRect.anchorMin = new Vector2(0.5f, 0.5f);
        handleRect.anchorMax = new Vector2(0.5f, 0.5f);
        handleRect.sizeDelta = new Vector2(20, 20);
        flowSlider.handleRect = handleRect;

        flowSlider.targetGraphic = handleImage;
    }
}
