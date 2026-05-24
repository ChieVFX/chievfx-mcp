using UnityEngine;
using UnityEngine.UIElements;

public class TestUIToolkit : MonoBehaviour
{
    private const int ItemCount = 31;
    private const int CenterItemIndex = ItemCount / 2;
    private const float InertiaDamping = 4.5f;
    private const float InertiaStopSpeed = 5f;

    private bool isDraggingScrollView;
    private float lastScrollPointerY;
    private float lastScrollPointerTime;
    private float scrollVelocityY;
    private VisualElement configuredRoot;
    private Button registeredButton;
    private ScrollView registeredScrollView;

    void Start()
    {
        SetupUi();
    }

    void Update()
    {
        SetupUi();
        ApplyScrollInertia();
    }

    void OnDisable()
    {
        if (registeredButton != null)
        {
            registeredButton.UnregisterCallback<ClickEvent>(OnButtonClick);
        }

        if (registeredScrollView != null)
        {
            UnregisterDragScrolling(registeredScrollView);
        }
    }

    private void SetupUi()
    {
        var document = GetComponent<UIDocument>();
        if (document == null)
        {
            Debug.LogError($"{nameof(TestUIToolkit)} requires a UIDocument on the same GameObject.");
            return;
        }

        var root = document.rootVisualElement;
        var button = root.Q<Button>("Button");
        var scrollView = root.Q<ScrollView>("ScrollView");

        if (button != null)
        {
            button.UnregisterCallback<ClickEvent>(OnButtonClick);
            button.RegisterCallback<ClickEvent>(OnButtonClick);
        }

        if (scrollView == null)
        {
            Debug.LogError("ScrollView not found.");
            return;
        }

        RegisterDragScrolling(scrollView);

        if (root == configuredRoot
            && button == registeredButton
            && scrollView == registeredScrollView
            && scrollView.contentContainer.childCount == ItemCount)
        {
            return;
        }

        configuredRoot = root;
        registeredButton = button;
        registeredScrollView = scrollView;

        FillScrollView(scrollView);
    }

    private static void OnButtonClick(ClickEvent evt)
    {
        Debug.Log("Test Button clicked");
    }

    private static void FillScrollView(ScrollView scrollView)
    {
        scrollView.mode = ScrollViewMode.Vertical;
        scrollView.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
        scrollView.verticalScrollerVisibility = ScrollerVisibility.Auto;
        scrollView.contentContainer.Clear();
        scrollView.contentContainer.style.flexDirection = FlexDirection.Column;

        for (var i = 0; i < ItemCount; i++)
        {
            var item = new Label($"Scroll item {i + 1}");
            item.style.height = 72;
            item.style.width = Length.Percent(100);
            item.style.marginBottom = 6;
            item.style.paddingLeft = 12;
            item.style.unityTextAlign = TextAnchor.MiddleLeft;
            item.style.backgroundColor = GetItemColor(i);
            item.style.color = Color.black;
            scrollView.Add(item);
        }
    }

    private static Color GetItemColor(int index)
    {
        if (index == 0)
        {
            return Color.blue;
        }

        if (index == CenterItemIndex)
        {
            return Color.green;
        }

        if (index == ItemCount - 1)
        {
            return Color.red;
        }

        return Color.white;
    }

    private void RegisterDragScrolling(ScrollView scrollView)
    {
        UnregisterDragScrolling(scrollView);

        scrollView.RegisterCallback<PointerDownEvent>(OnScrollPointerDown, TrickleDown.TrickleDown);
        scrollView.RegisterCallback<PointerMoveEvent>(OnScrollPointerMove, TrickleDown.TrickleDown);
        scrollView.RegisterCallback<PointerUpEvent>(OnScrollPointerUp, TrickleDown.TrickleDown);
        scrollView.RegisterCallback<PointerCancelEvent>(OnScrollPointerCancel, TrickleDown.TrickleDown);
    }

    private void UnregisterDragScrolling(ScrollView scrollView)
    {
        scrollView.UnregisterCallback<PointerDownEvent>(OnScrollPointerDown, TrickleDown.TrickleDown);
        scrollView.UnregisterCallback<PointerMoveEvent>(OnScrollPointerMove, TrickleDown.TrickleDown);
        scrollView.UnregisterCallback<PointerUpEvent>(OnScrollPointerUp, TrickleDown.TrickleDown);
        scrollView.UnregisterCallback<PointerCancelEvent>(OnScrollPointerCancel, TrickleDown.TrickleDown);
    }

    private void OnScrollPointerDown(PointerDownEvent evt)
    {
        if (evt.currentTarget is not ScrollView scrollView)
        {
            return;
        }

        if (IsScrollbarEvent(evt, scrollView))
        {
            isDraggingScrollView = false;
            scrollVelocityY = 0f;
            return;
        }

        isDraggingScrollView = true;
        lastScrollPointerY = evt.position.y;
        lastScrollPointerTime = Time.unscaledTime;
        scrollVelocityY = 0f;
        scrollView.CapturePointer(evt.pointerId);
        evt.StopPropagation();
    }

    private void OnScrollPointerMove(PointerMoveEvent evt)
    {
        if (evt.currentTarget is not ScrollView scrollView)
        {
            return;
        }

        if (!isDraggingScrollView || !scrollView.HasPointerCapture(evt.pointerId))
        {
            return;
        }

        var deltaY = lastScrollPointerY - evt.position.y;
        var deltaTime = Mathf.Max(Time.unscaledTime - lastScrollPointerTime, 0.001f);
        scrollVelocityY = deltaY / deltaTime;
        SetVerticalScrollOffset(scrollView, scrollView.scrollOffset.y + deltaY);
        lastScrollPointerY = evt.position.y;
        lastScrollPointerTime = Time.unscaledTime;
        evt.StopPropagation();
    }

    private void OnScrollPointerUp(PointerUpEvent evt)
    {
        if (evt.currentTarget is not ScrollView scrollView)
        {
            return;
        }

        isDraggingScrollView = false;
        scrollView.ReleasePointer(evt.pointerId);
        evt.StopPropagation();
    }

    private void OnScrollPointerCancel(PointerCancelEvent evt)
    {
        if (evt.currentTarget is not ScrollView scrollView)
        {
            return;
        }

        isDraggingScrollView = false;
        scrollVelocityY = 0f;
        scrollView.ReleasePointer(evt.pointerId);
        evt.StopPropagation();
    }

    private void ApplyScrollInertia()
    {
        if (registeredScrollView == null || isDraggingScrollView || Mathf.Abs(scrollVelocityY) < InertiaStopSpeed)
        {
            return;
        }

        var deltaTime = Time.unscaledDeltaTime;
        var previousOffset = registeredScrollView.scrollOffset.y;
        SetVerticalScrollOffset(registeredScrollView, previousOffset + scrollVelocityY * deltaTime);

        if (Mathf.Approximately(previousOffset, registeredScrollView.scrollOffset.y))
        {
            scrollVelocityY = 0f;
            return;
        }

        scrollVelocityY *= Mathf.Exp(-InertiaDamping * deltaTime);
    }

    private static void SetVerticalScrollOffset(ScrollView scrollView, float y)
    {
        scrollView.scrollOffset = new Vector2(0f, Mathf.Clamp(y, 0f, GetMaxScrollOffsetY(scrollView)));
    }

    private static float GetMaxScrollOffsetY(ScrollView scrollView)
    {
        return Mathf.Max(0f, scrollView.contentContainer.layout.height - scrollView.contentViewport.layout.height);
    }

    private static bool IsScrollbarEvent(EventBase evt, ScrollView scrollView)
    {
        var target = evt.target as VisualElement;
        while (target != null && target != scrollView)
        {
            if (target == scrollView.verticalScroller)
            {
                return true;
            }

            target = target.parent;
        }

        return false;
    }
}
