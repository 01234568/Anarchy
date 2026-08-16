// <copyright file="ForcedTunnelNodeElevationSystem.cs" company="Yenyang's Mods. MIT License">
// Copyright (c) Yenyang's Mods. MIT License. All rights reserved.
// </copyright>

namespace Anarchy.Systems.NetworkAnarchy
{
    using System.Collections.Generic;
    using Anarchy.Components;
    using Colossal.Entities;
    using Colossal.Mathematics;
    using Game;
    using Game.Common;
    using Game.Net;
    using Game.Prefabs;
    using Game.Simulation;
    using Game.Tools;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Mathematics;
    using UnityEngine;

    /// <summary>
    /// Reconciles forced-tunnel elevation on persistent network nodes after topology changes.
    /// </summary>
    public partial class ForcedTunnelNodeElevationSystem : GameSystemBase
    {
        private EntityQuery m_AppliedOrUpdatedEdgeQuery;
        private ModificationEndBarrier m_Barrier = null!;
        private TerrainSystem m_TerrainSystem = null!;

        /// <inheritdoc/>
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Barrier = World.GetOrCreateSystemManaged<ModificationEndBarrier>();
            m_TerrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_AppliedOrUpdatedEdgeQuery = SystemAPI.QueryBuilder()
                .WithAll<Edge>()
                .WithAny<Applied, Updated>()
                .WithNone<Deleted, Overridden, Temp, UpdateNextFrame, ClearUpdateNextFrame>()
                .Build();
            RequireForUpdate(m_AppliedOrUpdatedEdgeQuery);
        }

        /// <inheritdoc/>
        protected override void OnUpdate()
        {
            NativeArray<Entity> updatedEdges = m_AppliedOrUpdatedEdgeQuery.ToEntityArray(Allocator.Temp);
            HashSet<Entity> changedEdges = new HashSet<Entity>();
            HashSet<Entity> candidateNodes = new HashSet<Entity>();

            foreach (Entity entity in updatedEdges)
            {
                changedEdges.Add(entity);
                if (EntityManager.TryGetComponent(entity, out Edge edge))
                {
                    candidateNodes.Add(edge.m_Start);
                    candidateNodes.Add(edge.m_End);
                }
            }

            EntityCommandBuffer buffer = m_Barrier.CreateCommandBuffer();
            HashSet<Entity> topologyUpdates = new HashSet<Entity>();
            foreach (Entity edgeEntity in updatedEdges)
            {
                if (IsForcedTunnelEdge(edgeEntity))
                {
                    AddTopologyUpdateRing(edgeEntity, topologyUpdates);
                }
            }

            foreach (Entity nodeEntity in candidateNodes)
            {
                if (!EntityManager.Exists(nodeEntity) ||
                    EntityManager.HasComponent<Deleted>(nodeEntity) ||
                    EntityManager.HasComponent<Overridden>(nodeEntity) ||
                    EntityManager.HasComponent<Temp>(nodeEntity) ||
                    !EntityManager.TryGetBuffer(nodeEntity, isReadOnly: true, out DynamicBuffer<ConnectedEdge> connectedEdges))
                {
                    continue;
                }

                int connectedEdgeCount = 0;
                int connectedTunnelEdgeCount = 0;
                bool hasForcedTunnelEdge = false;
                bool hasChangedElevatedResetEdge = false;
                foreach (ConnectedEdge connectedEdge in connectedEdges)
                {
                    if (!IsValidPersistentEdge(connectedEdge.m_Edge))
                    {
                        continue;
                    }

                    connectedEdgeCount++;
                    hasForcedTunnelEdge |= IsForcedTunnelEdge(connectedEdge.m_Edge);
                    hasChangedElevatedResetEdge |= changedEdges.Contains(connectedEdge.m_Edge) &&
                        EntityManager.HasComponent<SetEndElevationsToZero>(connectedEdge.m_Edge) &&
                        EntityManager.TryGetComponent(connectedEdge.m_Edge, out Upgraded changedUpgraded) &&
                        (changedUpgraded.m_Flags.m_General & CompositionFlags.General.Elevated) == CompositionFlags.General.Elevated;
                    if (IsTunnelEdge(connectedEdge.m_Edge, changedEdges))
                    {
                        connectedTunnelEdgeCount++;
                    }
                }

                bool isInteriorTunnelNode = hasForcedTunnelEdge && connectedEdgeCount >= 2 &&
                    connectedTunnelEdgeCount == connectedEdgeCount;
                bool isTunnelDeadEnd = hasForcedTunnelEdge && connectedEdgeCount == 1 &&
                    connectedTunnelEdgeCount == 1;
                bool isTunnelNode = isInteriorTunnelNode || isTunnelDeadEnd;
                bool hasElevation = EntityManager.TryGetComponent(nodeEntity, out Elevation nodeElevation);
                bool stateChanged = false;

                if (isTunnelNode)
                {
                    float2 desiredElevation = nodeElevation.m_Elevation;
                    desiredElevation.x = Mathf.Min(desiredElevation.x, NetworkDefinitionSystem.TunnelThreshold);
                    desiredElevation.y = Mathf.Min(desiredElevation.y, NetworkDefinitionSystem.TunnelThreshold);

                    bool needsElevationChange = !hasElevation || math.any(nodeElevation.m_Elevation != desiredElevation);
                    if (needsElevationChange)
                    {
                        nodeElevation.m_Elevation = desiredElevation;
                        buffer.AddComponent(nodeEntity, nodeElevation);

                        stateChanged = true;
                    }
                }
                else if (hasForcedTunnelEdge &&
                    hasChangedElevatedResetEdge &&
                    hasElevation &&
                    math.all(nodeElevation.m_Elevation == new float2(NetworkDefinitionSystem.TunnelThreshold)))
                {
                    buffer.RemoveComponent<Elevation>(nodeEntity);

                    stateChanged = true;
                }

                if (stateChanged)
                {
                    topologyUpdates.Add(nodeEntity);
                    foreach (ConnectedEdge connectedEdge in connectedEdges)
                    {
                        if (IsValidPersistentEdge(connectedEdge.m_Edge))
                        {
                            topologyUpdates.Add(connectedEdge.m_Edge);
                        }
                    }
                }
            }

            foreach (Entity entity in topologyUpdates)
            {
                if (EntityManager.Exists(entity) &&
                    !EntityManager.HasComponent<Deleted>(entity) &&
                    !EntityManager.HasComponent<Overridden>(entity) &&
                    !EntityManager.HasComponent<Temp>(entity) &&
                    !EntityManager.HasComponent<UpdateNextFrame>(entity))
                {
                    buffer.AddComponent<UpdateNextFrame>(entity);
                }
            }

            InvalidateTerrain(topologyUpdates);
        }

        private static void AddBounds(Bounds3 bounds, ref Bounds2 updateBounds, ref bool hasBounds)
        {
            Bounds2 bounds2 = new Bounds2(bounds.min.xz, bounds.max.xz);
            if (!hasBounds)
            {
                updateBounds = bounds2;
                hasBounds = true;
                return;
            }

            updateBounds.min = math.min(updateBounds.min, bounds2.min);
            updateBounds.max = math.max(updateBounds.max, bounds2.max);
        }

        private void InvalidateTerrain(HashSet<Entity> topologyUpdates)
        {
            Bounds2 updateBounds = default;
            bool hasBounds = false;
            foreach (Entity entity in topologyUpdates)
            {
                if (EntityManager.TryGetComponent(entity, out EdgeGeometry edgeGeometry))
                {
                    AddBounds(edgeGeometry.m_Bounds, ref updateBounds, ref hasBounds);
                }

                if (EntityManager.TryGetComponent(entity, out StartNodeGeometry startNodeGeometry))
                {
                    AddBounds(startNodeGeometry.m_Geometry.m_Bounds, ref updateBounds, ref hasBounds);
                }

                if (EntityManager.TryGetComponent(entity, out EndNodeGeometry endNodeGeometry))
                {
                    AddBounds(endNodeGeometry.m_Geometry.m_Bounds, ref updateBounds, ref hasBounds);
                }

                if (EntityManager.TryGetComponent(entity, out NodeGeometry nodeGeometry))
                {
                    AddBounds(nodeGeometry.m_Bounds, ref updateBounds, ref hasBounds);
                }
            }

            if (hasBounds)
            {
                updateBounds.min -= new float2(16f);
                updateBounds.max += new float2(16f);
                m_TerrainSystem.OnAreaChanged(updateBounds);
            }
        }

        private bool IsValidPersistentEdge(Entity entity)
        {
            return EntityManager.Exists(entity) &&
                   !EntityManager.HasComponent<Deleted>(entity) &&
                   !EntityManager.HasComponent<Overridden>(entity) &&
                   !EntityManager.HasComponent<Temp>(entity);
        }

        private bool IsValidPersistentNode(Entity entity)
        {
            return EntityManager.Exists(entity) &&
                   EntityManager.HasComponent<Node>(entity) &&
                   !EntityManager.HasComponent<Deleted>(entity) &&
                   !EntityManager.HasComponent<Overridden>(entity) &&
                   !EntityManager.HasComponent<Temp>(entity);
        }

        private void AddTopologyUpdateRing(Entity edgeEntity, HashSet<Entity> topologyUpdates)
        {
            if (!IsValidPersistentEdge(edgeEntity) ||
                !EntityManager.TryGetComponent(edgeEntity, out Edge edge))
            {
                return;
            }

            topologyUpdates.Add(edgeEntity);
            AddNodeTopologyUpdateRing(edge.m_Start, topologyUpdates);
            AddNodeTopologyUpdateRing(edge.m_End, topologyUpdates);
        }

        private void AddNodeTopologyUpdateRing(Entity nodeEntity, HashSet<Entity> topologyUpdates)
        {
            if (!IsValidPersistentNode(nodeEntity))
            {
                return;
            }

            topologyUpdates.Add(nodeEntity);
            if (!EntityManager.TryGetBuffer(nodeEntity, isReadOnly: true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                return;
            }

            foreach (ConnectedEdge connectedEdge in connectedEdges)
            {
                if (!IsValidPersistentEdge(connectedEdge.m_Edge) ||
                    !EntityManager.TryGetComponent(connectedEdge.m_Edge, out Edge connectedEdgeData))
                {
                    continue;
                }

                topologyUpdates.Add(connectedEdge.m_Edge);
                if (IsValidPersistentNode(connectedEdgeData.m_Start))
                {
                    topologyUpdates.Add(connectedEdgeData.m_Start);
                }

                if (IsValidPersistentNode(connectedEdgeData.m_End))
                {
                    topologyUpdates.Add(connectedEdgeData.m_End);
                }
            }
        }

        private bool IsTunnelEdge(Entity entity, HashSet<Entity> changedEdges)
        {
            if (EntityManager.TryGetComponent(entity, out Upgraded upgraded))
            {
                if ((upgraded.m_Flags.m_General & CompositionFlags.General.Tunnel) == CompositionFlags.General.Tunnel)
                {
                    return true;
                }
            }

            if (changedEdges.Contains(entity) &&
                EntityManager.HasComponent<SetEndElevationsToZero>(entity))
            {
                return false;
            }

            return EntityManager.TryGetComponent(entity, out Composition composition) &&
                   EntityManager.TryGetComponent(composition.m_Edge, out NetCompositionData compositionData) &&
                   (compositionData.m_Flags.m_General & CompositionFlags.General.Tunnel) == CompositionFlags.General.Tunnel;
        }

        private bool IsForcedTunnelEdge(Entity entity)
        {
            return EntityManager.TryGetComponent(entity, out Upgraded upgraded) &&
                   (upgraded.m_Flags.m_General & CompositionFlags.General.Tunnel) == CompositionFlags.General.Tunnel;
        }
    }
}
