using System.Collections.Generic;
using UnityEngine;

public class CopyTransform : MonoBehaviour
{
    [Header("Source Transforms (to copy from)")]
    public List<Transform> sourceTransforms = new List<Transform>();

    [Header("Target Transforms (to copy to)")]
    public List<Transform> targetTransforms = new List<Transform>();

    [Header("Matched and Sorted Lists")]
    public List<Transform> sortedSourceTransforms = new List<Transform>();
    public List<Transform> sortedTargetTransforms = new List<Transform>();

    public void Start()
    {
        // Clear previous sorted lists
        sortedSourceTransforms.Clear();
        sortedTargetTransforms.Clear();

        // Match and sort transforms by name
        foreach (Transform source in sourceTransforms)
        {
            foreach (Transform target in targetTransforms)
            {
                if (source != null && target != null && source.name == target.name)
                {
                    sortedSourceTransforms.Add(source);
                    sortedTargetTransforms.Add(target);
                    break; // Avoid duplicate matches for the same target
                }
            }
        }
    }

    void Update()
    {
        // Ensure both sorted lists are the same length
        int length = Mathf.Min(sortedSourceTransforms.Count, sortedTargetTransforms.Count);

        for (int i = 0; i < length; i++)
        {
            if (sortedSourceTransforms[i] != null && sortedTargetTransforms[i] != null)
            {
                // Copy position, rotation, and scale
                sortedTargetTransforms[i].position = sortedSourceTransforms[i].position;
                sortedTargetTransforms[i].rotation = sortedSourceTransforms[i].rotation;
                sortedTargetTransforms[i].localScale = sortedSourceTransforms[i].localScale;
            }
        }
    }
}
