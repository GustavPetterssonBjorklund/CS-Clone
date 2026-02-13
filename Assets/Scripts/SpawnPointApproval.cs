using Unity.Netcode;
using UnityEngine;
public class SpawnPointApproval : MonoBehaviour
{
    [SerializeField] private Transform[] spawnPoints;
    [SerializeField, Min(0.05f)] private float spawnHeightOffset = 1.1f;
    [SerializeField, Min(1f)] private float groundProbeDistance = 200f;
    [SerializeField] private LayerMask groundMask = ~0;
    [SerializeField] private bool logSpawnDecisions = true;

    private int nextIndex;
    private NetworkManager nm;

    private void Awake()
    {
        nm = GetComponent<NetworkManager>();
        nm.ConnectionApprovalCallback = ApprovalCheck;
    }

    private void ApprovalCheck(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse
response)
    {
        Transform sp = (spawnPoints != null && spawnPoints.Length > 0)
            ? spawnPoints[nextIndex++ % spawnPoints.Length]
            : null;

        response.Approved = true;
        response.CreatePlayerObject = true;
        response.PlayerPrefabHash = null;
        Vector3? resolvedPosition = sp != null
            ? ResolveSpawnPosition(sp.position)
            : (Vector3?)null;
        response.Position = resolvedPosition;
        response.Rotation = sp != null ? sp.rotation : (Quaternion?)null;
        response.Pending = false;

        if (logSpawnDecisions)
        {
            Debug.Log(
                $"SpawnPointApproval: client={request.ClientNetworkId} " +
                $"spawn={(sp != null ? sp.name : "<none>")} " +
                $"requested={(sp != null ? sp.position.ToString() : "<null>")} " +
                $"resolved={(resolvedPosition.HasValue ? resolvedPosition.Value.ToString() : "<null>")}");
        }
    }

    private Vector3 ResolveSpawnPosition(Vector3 requestedPosition)
    {
        // Start the ray above the requested point so we can safely snap to terrain/ground colliders.
        Vector3 rayOrigin = requestedPosition + Vector3.up * (groundProbeDistance * 0.5f);
        if (Physics.Raycast(
            rayOrigin,
            Vector3.down,
            out RaycastHit hit,
            groundProbeDistance,
            groundMask,
            QueryTriggerInteraction.Ignore))
        {
            return hit.point + Vector3.up * spawnHeightOffset;
        }

        // Fallback when no collider is found below this spawn point.
        return requestedPosition + Vector3.up * spawnHeightOffset;
    }
}
