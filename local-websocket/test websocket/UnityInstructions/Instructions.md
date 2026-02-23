Got it! Here’s a **streamlined set of instructions** based on your setup where the cube already exists:

---

## **Step 1 — Create the C# Script**

1. In the **Project window**, go to `Assets > Scripts`.
2. Right-click in the folder → **Create → Scripting → Empty C# Script**.
3. Name the script `PCWebSocketClient2D`.
4. Double-click the script to open it in your editor (VS Code or Unity default).

---

## **Step 2 — Add the WebSocket Code**

1. Delete all the default content in the new script.
2. Copy the **full `PCWebSocketClient2D` code** 
3. Save the file.

---

## **Step 3 — Attach the Script to the Cube**

1. Select your existing cube in the **Hierarchy**.
2. Drag the `PCWebSocketClient2D` script from the Project window onto the cube in the **Inspector**.
3. The script should now appear as a **component** on the cube.

---

## **Step 4 — Configure the Component**

1. In the cube’s Inspector, under the script:

   * **Server Url:** Set to your Node WebSocket server, e.g.,

    * `ws://127.0.0.1:8080`
    * or `ws://localhost:8080`

   * **Move Speed:** Set a value like `10` or `20` for visible movement.
   * **Movement Settings:** >20

---

## **Step 5 — Ensure Rigidbody2D is Correct**

* Your cube must have a **Rigidbody2D** component.
* **Body Type:** Kinematic
* **Constraints:** None checked

---

## **Step 6 — Run and Test**

1. Start your **Node WebSocket server**.
2. Press **Play** in Unity.
3. Check the **Console** for:

   * “Connected to WebSocket server”
   * Incoming messages like: `Received message: {"command":"right","confidence":0.9}`
4. The cube should now **move smoothly** based on the received commands.



