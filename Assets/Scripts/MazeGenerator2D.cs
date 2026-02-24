using UnityEngine;
using UnityEngine.Tilemaps;
using System.Collections.Generic;

/// <summary>
/// Generates 2D procedural mazes using tiles or sprites
/// Creates visually appealing maze layouts for top-down gameplay
/// </summary>
public class MazeGenerator2D : MonoBehaviour
{
    [Header("Maze Dimensions")]
    [SerializeField] private int width = 10;
    [SerializeField] private int height = 10;
    [SerializeField] private float cellSize = 1f;

    [Header("Prefabs/Sprites")]
    [SerializeField] private GameObject wallPrefab;
    [SerializeField] private GameObject floorPrefab;
    [SerializeField] private GameObject collectiblePrefab;
    [SerializeField] private GameObject goalPrefab;
    
    [Header("Alternative: Use Tilemap")]
    [SerializeField] private bool useTilemap = false;
    [SerializeField] private Tilemap wallTilemap;
    [SerializeField] private Tilemap floorTilemap;
    [SerializeField] private TileBase wallTile;
    [SerializeField] private TileBase floorTile;
    
    [Header("Generation Settings")]
    [SerializeField] private int collectibleCount = 5;
    [SerializeField] private bool showGenerationDebug = false;

    private int[,] maze;
    private List<Vector2Int> visitedCells = new List<Vector2Int>();
    private GameObject mazeParent;

    public Vector3 GetStartPosition()
    {
        if (maze == null)
        {
            return new Vector3(cellSize * 0.5f, cellSize * 0.5f, 0);
        }

        // Find first open cell (maze generation always starts at 0,0)
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                if (maze[x, y] == 1) // Open path
                {
                    Vector3 pos = new Vector3(x * cellSize + cellSize * 0.5f, y * cellSize + cellSize * 0.5f, 0);
                    Debug.Log($"<color=cyan>[START] Position: {pos}</color>");
                    return pos;
                }
            }
        }

        // Fallback
        return new Vector3(cellSize * 0.5f, cellSize * 0.5f, 0);
    }

    public Vector3 GetGoalPosition()
    {
        if (maze == null)
        {
            Debug.LogError("[GOAL] Maze not generated yet!");
            return new Vector3((width - 1) * cellSize + cellSize * 0.5f, (height - 1) * cellSize + cellSize * 0.5f, 0);
        }

        // Get start cell coordinates
        Vector3 startWorldPos = GetStartPosition();
        Vector2Int startCell = new Vector2Int(
            Mathf.FloorToInt(startWorldPos.x / cellSize),
            Mathf.FloorToInt(startWorldPos.y / cellSize)
        );

        // Find furthest reachable cell using BFS (Breadth-First Search)
        Vector2Int furthestCell = FindFurthestCell(startCell);

        Vector3 goalPos = new Vector3(
            furthestCell.x * cellSize + cellSize * 0.5f,
            furthestCell.y * cellSize + cellSize * 0.5f,
            0
        );

        Debug.Log($"<color=yellow>[GOAL] Furthest reachable cell: {furthestCell} at {goalPos}</color>");
        return goalPos;
    }

    Vector2Int FindFurthestCell(Vector2Int startCell)
    {
        // BFS to find furthest reachable cell
        Queue<Vector2Int> queue = new Queue<Vector2Int>();
        Dictionary<Vector2Int, int> distances = new Dictionary<Vector2Int, int>();

        queue.Enqueue(startCell);
        distances[startCell] = 0;

        Vector2Int furthestCell = startCell;
        int maxDistance = 0;

        Vector2Int[] directions = {
        new Vector2Int(0, 1),   // Up
        new Vector2Int(0, -1),  // Down
        new Vector2Int(1, 0),   // Right
        new Vector2Int(-1, 0)   // Left
    };

        while (queue.Count > 0)
        {
            Vector2Int current = queue.Dequeue();
            int currentDistance = distances[current];

            // Check if this is the furthest we've found
            if (currentDistance > maxDistance)
            {
                maxDistance = currentDistance;
                furthestCell = current;
            }

            // Explore neighbors
            foreach (Vector2Int dir in directions)
            {
                Vector2Int neighbor = current + dir;

                // Check if neighbor is valid and not visited
                if (IsValidCell(neighbor.x, neighbor.y) &&
                    maze[neighbor.x, neighbor.y] == 1 &&
                    !distances.ContainsKey(neighbor))
                {
                    queue.Enqueue(neighbor);
                    distances[neighbor] = currentDistance + 1;
                }
            }
        }

        Debug.Log($"<color=yellow>[PATHFINDING] Furthest cell is {maxDistance} steps away</color>");
        return furthestCell;
    }

    public void GenerateMaze()
    {
        ClearMaze();
        
        maze = new int[width, height];
        mazeParent = new GameObject("Maze");
        
        // Generate maze using recursive backtracking
        GenerateMazeRecursive(0, 0);
        
        // Build the 2D maze
        if (useTilemap && wallTilemap != null && floorTilemap != null)
        {
            BuildMazeTilemap();
        }
        else
        {
            BuildMazeSprites();
        }
        
        // Place collectibles
        PlaceCollectibles();
        
        // Place goal
        PlaceGoal();
        
        Debug.Log($"<color=green>[2D MAZE] Generated {width}x{height} maze</color>");
    }

    void GenerateMazeRecursive(int x, int y)
    {
        visitedCells.Add(new Vector2Int(x, y));
        maze[x, y] = 1; // Mark as path

        // Get random directions
        List<Vector2Int> directions = GetShuffledDirections();

        foreach (Vector2Int dir in directions)
        {
            int nx = x + dir.x * 2;
            int ny = y + dir.y * 2;

            if (IsValidCell(nx, ny) && maze[nx, ny] == 0)
            {
                // Remove wall between cells
                maze[x + dir.x, y + dir.y] = 1;
                
                GenerateMazeRecursive(nx, ny);
            }
        }
    }

    List<Vector2Int> GetShuffledDirections()
    {
        List<Vector2Int> directions = new List<Vector2Int>
        {
            new Vector2Int(0, 1),  // Up
            new Vector2Int(1, 0),  // Right
            new Vector2Int(0, -1), // Down
            new Vector2Int(-1, 0)  // Left
        };

        // Shuffle
        for (int i = 0; i < directions.Count; i++)
        {
            Vector2Int temp = directions[i];
            int randomIndex = Random.Range(i, directions.Count);
            directions[i] = directions[randomIndex];
            directions[randomIndex] = temp;
        }

        return directions;
    }

    bool IsValidCell(int x, int y)
    {
        return x >= 0 && x < width && y >= 0 && y < height;
    }

    void BuildMazeSprites()
    {
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                Vector3 position = new Vector3(x * cellSize + cellSize * 0.5f, y * cellSize + cellSize * 0.5f, 0);
                
                // Create floor for all cells
                if (floorPrefab != null)
                {
                    GameObject floor = Instantiate(floorPrefab, position, Quaternion.identity, mazeParent.transform);
                    floor.name = $"Floor_{x}_{y}";
                    
                    // Ensure floor is behind everything
                    SpriteRenderer sr = floor.GetComponent<SpriteRenderer>();
                    if (sr != null) sr.sortingOrder = -1;
                }

                // Create walls
                if (maze[x, y] == 0)
                {
                    CreateWall(position, x, y);
                }
            }
        }

        // Create outer walls
        CreateOuterWalls();
    }

    void BuildMazeTilemap()
    {
        floorTilemap.ClearAllTiles();
        wallTilemap.ClearAllTiles();

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                Vector3Int tilePos = new Vector3Int(x, y, 0);
                
                // Place floor tile
                if (floorTile != null)
                {
                    floorTilemap.SetTile(tilePos, floorTile);
                }

                // Place wall tile
                if (maze[x, y] == 0 && wallTile != null)
                {
                    wallTilemap.SetTile(tilePos, wallTile);
                }
            }
        }

        // Create outer walls with tilemap
        for (int x = -1; x <= width; x++)
        {
            wallTilemap.SetTile(new Vector3Int(x, -1, 0), wallTile);
            wallTilemap.SetTile(new Vector3Int(x, height, 0), wallTile);
        }
        for (int y = 0; y < height; y++)
        {
            wallTilemap.SetTile(new Vector3Int(-1, y, 0), wallTile);
            wallTilemap.SetTile(new Vector3Int(width, y, 0), wallTile);
        }
    }

    void CreateWall(Vector3 position, int x, int y)
    {
        if (wallPrefab != null)
        {
            GameObject wall = Instantiate(wallPrefab, position, Quaternion.identity, mazeParent.transform);
            wall.transform.localScale = new Vector3(cellSize, cellSize, 1);
            wall.layer = LayerMask.NameToLayer("Wall");
            wall.tag = "Wall";
            wall.name = $"Wall_{x}_{y}";
            
            // Ensure wall is on top of floor
            SpriteRenderer sr = wall.GetComponent<SpriteRenderer>();
            if (sr != null) sr.sortingOrder = 0;
        }
    }

    void CreateOuterWalls()
    {
        // Top and bottom walls
        for (int x = -1; x <= width; x++)
        {
            Vector3 topPos = new Vector3(x * cellSize + cellSize * 0.5f, -cellSize * 0.5f, 0);
            Vector3 bottomPos = new Vector3(x * cellSize + cellSize * 0.5f, height * cellSize + cellSize * 0.5f, 0);
            
            if (wallPrefab != null)
            {
                GameObject topWall = Instantiate(wallPrefab, topPos, Quaternion.identity, mazeParent.transform);
                topWall.transform.localScale = new Vector3(cellSize, cellSize, 1);
                
                GameObject bottomWall = Instantiate(wallPrefab, bottomPos, Quaternion.identity, mazeParent.transform);
                bottomWall.transform.localScale = new Vector3(cellSize, cellSize, 1);
            }
        }

        // Left and right walls
        for (int y = 0; y < height; y++)
        {
            Vector3 leftPos = new Vector3(-cellSize * 0.5f, y * cellSize + cellSize * 0.5f, 0);
            Vector3 rightPos = new Vector3(width * cellSize + cellSize * 0.5f, y * cellSize + cellSize * 0.5f, 0);
            
            if (wallPrefab != null)
            {
                GameObject leftWall = Instantiate(wallPrefab, leftPos, Quaternion.identity, mazeParent.transform);
                leftWall.transform.localScale = new Vector3(cellSize, cellSize, 1);
                
                GameObject rightWall = Instantiate(wallPrefab, rightPos, Quaternion.identity, mazeParent.transform);
                rightWall.transform.localScale = new Vector3(cellSize, cellSize, 1);
            }
        }
    }

    void PlaceCollectibles()
    {
        if (collectiblePrefab == null) return;

        int placed = 0;
        int attempts = 0;
        int maxAttempts = collectibleCount * 10;

        while (placed < collectibleCount && attempts < maxAttempts)
        {
            int x = Random.Range(1, width - 1);
            int y = Random.Range(1, height - 1);

            if (maze[x, y] == 1)
            {
                Vector3 position = new Vector3(x * cellSize + cellSize * 0.5f, y * cellSize + cellSize * 0.5f, 0);
                GameObject collectible = Instantiate(collectiblePrefab, position, Quaternion.identity, mazeParent.transform);
                
                // Ensure collectible is visible
                SpriteRenderer sr = collectible.GetComponent<SpriteRenderer>();
                if (sr != null) sr.sortingOrder = 1;
                
                placed++;
            }

            attempts++;
        }

        Debug.Log($"<color=cyan>[2D MAZE] Placed {placed} collectibles</color>");
    }

    void PlaceGoal()
    {
        if (goalPrefab != null)
        {
            Vector3 goalPos = GetGoalPosition();
            GameObject goal = Instantiate(goalPrefab, goalPos, Quaternion.identity, mazeParent.transform);
            goal.tag = "Goal";
            
            // Ensure goal is visible
            SpriteRenderer sr = goal.GetComponent<SpriteRenderer>();
            if (sr != null) sr.sortingOrder = 1;
        }
    }

    void ClearMaze()
    {
        if (mazeParent != null)
        {
            DestroyImmediate(mazeParent);
        }
        
        if (useTilemap)
        {
            if (wallTilemap != null) wallTilemap.ClearAllTiles();
            if (floorTilemap != null) floorTilemap.ClearAllTiles();
        }
        
        visitedCells.Clear();
    }

    void OnDrawGizmos()
    {
        if (!showGenerationDebug || maze == null) return;

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                Vector3 position = new Vector3(x * cellSize + cellSize * 0.5f, y * cellSize + cellSize * 0.5f, 0);
                Gizmos.color = maze[x, y] == 1 ? Color.white : Color.black;
                Gizmos.DrawCube(position, Vector2.one * cellSize * 0.8f);
            }
        }
    }
}
