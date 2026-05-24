using UnityEngine;
using UnityEngine.UI;

public class TestUGui : MonoBehaviour
{
    public Button button;

    void Start()
    {
        button.onClick.AddListener(OnButtonClick);
    }

    void OnButtonClick()
    {
        Debug.Log("Test Button clicked");
    }
}
