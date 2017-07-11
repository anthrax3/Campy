﻿using Swigged.LLVM;

namespace Campy.ControlFlowGraph
{
    using Campy.Graphs;
    using Mono.Cecil.Cil;
    using Mono.Cecil;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System;

    public class CFG : GraphLinkedList<int, CFG.Vertex, CFG.Edge>
    {


        private static int _node_number = 1;

        public int NewNodeNumber()
        {
            return _node_number++;
        }

        private List<CFG.Vertex> _entries = new List<Vertex>();

        public List<CFG.Vertex> Entries
        {
            get { return _entries; }
        }

        public CFG()
            : base()
        {
        }

        private Dictionary<int, List<CFG.Vertex>> _change_set = new Dictionary<int, List<Vertex>>();
        private Random random;
        
        public int StartChangeSet()
        {
            if (random == null)
            {
                random = new Random();
            }
            int new_num = 0;
            for (;;)
            {
                new_num = random.Next(100000000);
                if (_change_set.ContainsKey(new_num))
                {
                    continue;
                }
                break;
            }
            _change_set.Add(new_num, new List<Vertex>());
            return new_num;
        }

        public List<CFG.Vertex> PopChangeSet(int num)
        {
            if (_change_set.ContainsKey(num))
            {
                List<CFG.Vertex> list = _change_set[num];
                _change_set.Remove(num);
                return list;
            }
            throw new Exception("Unknown change set.");
        }

        public override IVertex<int> AddVertex(int v)
        {
            foreach (CFG.Vertex vertex in this.VertexNodes)
            {
                if (vertex.Name == v)
                    return vertex;
            }
            CFG.Vertex x = (Vertex)base.AddVertex(v);
            foreach (KeyValuePair<int, List<CFG.Vertex>> pair in this._change_set)
            {
                pair.Value.Add(x);
                Debug.Assert(_change_set[pair.Key].Contains(x));
            }
            return x;
        }

        public Vertex FindEntry(Inst inst)
        {
            Vertex result = null;

            // Find owning block.
            result = inst.Block;

            // Return entry block for method.
            return result.Entry;
        }

        public Vertex FindEntry(Mono.Cecil.Cil.Instruction inst)
        {
            Vertex result = null;
            foreach (CFG.Vertex node in this.VertexNodes)
                if (node.Instructions.First().Instruction == inst)
                    return node;
            return result;
        }

        public Vertex FindEntry(Mono.Cecil.MethodReference mr)
        {
            foreach (CFG.Vertex node in this.VertexNodes)
                if (node.Method == mr)
                    return node;
            return null;
        }

        public class Vertex
            : GraphLinkedList<int, Vertex, Edge>.Vertex
        {        
            public MethodDefinition Method { get; set; }
            public List<Inst> Instructions { get; private set; } = new List<Inst>();

            public BasicBlockRef BasicBlock { get; set; }
            public ValueRef Function { get; set; }
            public BuilderRef Builder { get; set; }
            public ModuleRef Module { get; set; }

            public bool HasReturnValue { get; set; }

            public int? StackLevelIn { get; set; }

            public int? StackLevelOut { get; set; }

            public State StateIn { get; set; }

            public State StateOut { get; set; }

            private Vertex _entry;
            public Vertex Entry
            {
                get { return _entry; }
                set { _entry = value; }
            }

            public bool IsEntry
            {
                get
                {
                    if (_entry == null)
                        return false;
                    if (_entry._ordered_list_of_blocks == null)
                        return false;
                    if (_entry._ordered_list_of_blocks.Count == 0)
                        return false;
                    if (_entry._ordered_list_of_blocks.First() != this)
                        return false;
                    return true;
                }
            }

            public bool IsCall
            {
                get
                {
                    Inst last = Instructions[Instructions.Count - 1];
                    switch (last.OpCode.FlowControl)
                    {
                        case FlowControl.Call:
                            return true;
                        default:
                            return false;
                    }
                }
            }

            public bool IsNewobj
            {
                get
                {
                    Inst last = Instructions[Instructions.Count - 1];
                    return last.OpCode.Code == Code.Newobj;
                }
            }

            public bool IsNewarr
            {
                get
                {
                    Inst last = Instructions[Instructions.Count - 1];
                    return last.OpCode.Code == Code.Newarr;
                }
            }

            public bool IsReturn
            {
                get
                {
                    Inst last = Instructions[Instructions.Count - 1];
                    switch (last.OpCode.FlowControl)
                    {
                        case FlowControl.Return:
                            return true;
                        default:
                            return false;
                    }
                }
            }

            public int NumberOfLocals { get; set; }

            public int NumberOfArguments { get; set; }

            public Vertex Exit
            {
                get
                {
                    List<Vertex> list = this._entry._ordered_list_of_blocks;
                    return list[list.Count() - 1];
                }
            }

            public List<Vertex> _ordered_list_of_blocks;

            public Vertex()
                : base()
            {
            }

            public void OutputEntireNode()
            {
                CFG.Vertex v = this;
                Console.WriteLine();
                Console.WriteLine("Node: " + v.Name + " ");
                Console.WriteLine(new String(' ', 4) + "Method " + v.Method.FullName);
                Console.WriteLine(new String(' ', 4) + "Args   " + v.NumberOfArguments);
                Console.WriteLine(new String(' ', 4) + "Locals " + v.NumberOfLocals);
                Console.WriteLine(new String(' ', 4) + "Return (reuse) " + v.HasReturnValue);
                if (this._Graph.Predecessors(v.Name).Any())
                {
                    Console.Write(new String(' ', 4) + "Edges from:");
                    foreach (object t in this._Graph.Predecessors(v.Name))
                    {
                        Console.Write(" " + t);
                    }
                    Console.WriteLine();
                }
                if (this._Graph.Successors(v.Name).Any())
                {
                    Console.Write(new String(' ', 4) + "Edges to:");
                    foreach (object t in this._Graph.Successors(v.Name))
                    {
                        Console.Write(" " + t);
                    }
                    Console.WriteLine();
                }
                Console.WriteLine(new String(' ', 4) + "Instructions:");
                foreach (Inst i in v.Instructions)
                {
                    Console.Write(new String(' ', 8) + i + new String(' ', 4));
                    Console.WriteLine();
                }
                Console.WriteLine();
            }

            public Vertex Split(int i)
            {
                Debug.Assert(Instructions.Count != 0);
                // Split this node into two nodes, with all instructions after "i" in new node.
                var cfg = (CFG)this._Graph;
                Vertex result = (Vertex)cfg.AddVertex(cfg.NewNodeNumber());
                result.Method = this.Method;
                result.HasReturnValue = this.HasReturnValue;
                result._entry = this._entry;

                // Insert new block after this block.
                this._entry._ordered_list_of_blocks.Insert(
                    this._entry._ordered_list_of_blocks.IndexOf(this) + 1,
                    result);

                int count = Instructions.Count;

                // Add instructions from split point to new block.
                for (int j = i; j < count; ++j)
                {
                    Inst newInst = Inst.Wrap(Instructions[j].Instruction);
                    CFG.Vertex v = newInst.Block;
                    newInst.Block = (CFG.Vertex) result;
                    result.Instructions.Add(newInst);
                }

                // Remove instructions from previous block.
                for (int j = i; j < count; ++j)
                {
                    this.Instructions.RemoveAt(i);
                }

                Debug.Assert(this.Instructions.Count != 0);
                Debug.Assert(result.Instructions.Count != 0);
                Debug.Assert(this.Instructions.Count + result.Instructions.Count == count);

                Inst last_instruction = this.Instructions[
                    this.Instructions.Count - 1];

                // Transfer any out edges to pred block to new block.
                while (cfg.SuccessorNodes(this).Count() > 0)
                {
                    CFG.Vertex succ = cfg.SuccessorNodes(this).First();
                    cfg.DeleteEdge(this, succ);
                    cfg.AddEdge(result, succ);
                }

                // Add fall-through branch from pred to succ block.
                switch (last_instruction.OpCode.FlowControl)
                {
                    case FlowControl.Branch:
                        break;
                    case FlowControl.Break:
                        break;
                    case FlowControl.Call:
                        break;
                    case FlowControl.Cond_Branch:
                        cfg.AddEdge(this.Name, result.Name);
                        break;
                    case FlowControl.Meta:
                        break;
                    case FlowControl.Next:
                        cfg.AddEdge(this.Name, result.Name);
                        break;
                    case FlowControl.Phi:
                        break;
                    case FlowControl.Return:
                        break;
                    case FlowControl.Throw:
                        break;
                }

                //System.Console.WriteLine("After split");
                //cfg.Dump();
                //System.Console.WriteLine("-----------");
                return result;
            }
        }

        public class Edge
            : GraphLinkedList<int, CFG.Vertex, CFG.Edge>.Edge
        {
            public bool IsInterprocedural()
            {
                CFG.Vertex f = (CFG.Vertex)this.from;
                CFG.Vertex t = (CFG.Vertex)this.to;
                if (f.Method != t.Method)
                    return true;
                return false;
            }
        }

        public void OutputEntireGraph()
        {
            System.Console.WriteLine("Graph:");
            System.Console.WriteLine();
            System.Console.WriteLine("List of entries blocks:");
            System.Console.WriteLine(new String(' ', 4) + "Node" + new string(' ', 4) + "Method");
            foreach (Vertex n in this._entries)
            {
                System.Console.Write("{0,8}", n);
                System.Console.Write(new string(' ', 4));
                System.Console.WriteLine(n.Method.FullName);
            }
            System.Console.WriteLine();
            System.Console.WriteLine("List of callers:");
            System.Console.WriteLine(new String(' ', 4) + "Node" + new string(' ', 4) + "Instruction");
            foreach (Inst caller in Inst.CallInstructions)
            {
                Vertex n = caller.Block;
                System.Console.Write("{0,8}", n);
                System.Console.Write(new string(' ', 4));
                System.Console.WriteLine(caller);
            }
            if (this._entries.Any())
            {
                System.Console.WriteLine();
                System.Console.WriteLine("List of orphan blocks:");
                System.Console.WriteLine(new String(' ', 4) + "Node" + new string(' ', 4) + "Method");
                System.Console.WriteLine();
            }

            foreach (Vertex n in VertexNodes)
            {
                if (n._ordered_list_of_blocks != null)
                {
                    foreach (Vertex v in n._ordered_list_of_blocks)
                    {
                        v.OutputEntireNode();
                    }
                }
            }
        }

        public void OutputDotGraph()
        {
            Dictionary<int,bool> visited = new Dictionary<int, bool>();
            System.Console.WriteLine("digraph {");
            foreach (IEdge<int> n in this.Edges)
            {
                System.Console.WriteLine(n.From + " -> " + n.To + ";");
                visited[n.From] = true;
                visited[n.To] = true;
            }
            foreach (int n in this.Vertices)
            {
                if (visited.ContainsKey(n)) continue;
                System.Console.WriteLine(n + ";");
            }
            System.Console.WriteLine("}");
            System.Console.WriteLine();
        }

        class CallEnumerator : IEnumerable<CFG.Vertex>
        {
            CFG.Vertex _node;

            public CallEnumerator(CFG.Vertex node)
            {
                _node = node;
            }

            public IEnumerator<CFG.Vertex> GetEnumerator()
            {
                foreach (CFG.Vertex current in _node._ordered_list_of_blocks)
                {
                    foreach (CFG.Vertex next in _node._Graph.SuccessorNodes(current))
                    {
                        if (next.IsEntry && next.Method != _node.Method)
                            yield return next;
                    }
                }
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }

        public IEnumerable<CFG.Vertex> AllInterproceduralCalls(CFG.Vertex node)
        {
            return new CallEnumerator(node);
        }

    }
}