﻿using System;
using System.Collections.Generic;
using System.Linq;
using Campy.Utils;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using Swigged.LLVM;

namespace Campy.Compiler
{
    public class STATE
    {
        // See ECMA 335, page 82.
        public StackQueue<VALUE> _stack;
        public ListSection<VALUE> _struct_ret; // Pointer to _stack, if there is a "this" pointer.
        public ListSection<VALUE> _this; // Pointer to _stack, if there is a "this" pointer.
        public ListSection<VALUE> _arguments; // Pointer to _stack, if there are parameters for the method.
        public ListSection<VALUE> _locals; // Pointer to _stack, if there are local variables to the method.
        public List<ValueRef> _phi;

        public STATE()
        {
            _stack = new StackQueue<VALUE>();
            _this = null;
            _arguments = null;
            _locals = null;
            _struct_ret = null;
            _phi = new List<ValueRef>();
        }

        public STATE(Dictionary<CFG.Vertex, bool> visited,
            Dictionary<CFG.Vertex, STATE> states_in,
            Dictionary<CFG.Vertex, STATE> states_out,
            CFG.Vertex bb)
        {
            // Set up a state that is a copy of another state.
            _stack = new StackQueue<VALUE>();
            int in_level = -1;

            int args = bb.StackNumberOfArguments;
            bool scalar_ret = bb.HasScalarReturnValue;
            bool struct_ret = bb.HasStructReturnValue;
            bool has_this = bb.HasThis;
            int locals = bb.StackNumberOfLocals;
            // Use predecessor information to get initial stack size.
            if (bb.IsEntry)
            {
                in_level = bb.StackNumberOfLocals + bb.StackNumberOfArguments;
            }
            else
            {
                foreach (CFG.Vertex pred in bb._graph.PredecessorNodes(bb))
                {
                    // Do not consider interprocedural edges when computing stack size.
                    if (pred._original_method_reference != bb._original_method_reference)
                        throw new Exception("Interprocedural edge should not exist.");
                    // If predecessor has not been visited, warn and do not consider.
                    // Warn if predecessor does not concur with another predecessor.
                    if (in_level != -1 && states_out[pred]._stack.Count != in_level)
                        throw new Exception("Miscalculation in stack size.");
                    in_level = states_out[pred]._stack.Count;
                }
            }
            if (in_level == -1)
            {
                throw new Exception("Predecessor edge computation screwed up.");
            }

            int level = in_level;

            // Set up list of phi functions in case there are multiple predecessors.
            _phi = new List<ValueRef>();

            // State depends on predecessors. To handle this without updating state
            // until a fix point is found while converting to LLVM IR, we introduce
            // SSA phi functions.
            if (bb._graph.PredecessorNodes(bb).Count() == 0)
            {
                if (!bb.IsEntry) throw new Exception("Cannot handle dead code blocks.");
                var fun = bb.LlvmInfo.MethodValueRef;
                var t_fun = LLVM.TypeOf(fun);
                var t_fun_con = LLVM.GetTypeContext(t_fun);
                var context = LLVM.GetModuleContext(JITER.global_llvm_module);
                if (t_fun_con != context) throw new Exception("not equal");

                for (uint i = 0; i < args; ++i)
                {
                    var par = new VALUE(LLVM.GetParam(fun, i));
                    if (Campy.Utils.Options.IsOn("jit_trace"))
                        System.Console.WriteLine(par);
                    _stack.Push(par);
                }

                int offset = 0;
                _struct_ret = _stack.Section(struct_ret ? offset++ : offset, struct_ret ? 1 : 0);
                _this = _stack.Section(has_this ? offset++ : offset, has_this ? 1 : 0);
                // Set up args. NB: arg 0 is "this" pointer according to spec!!!!!
                _arguments = _stack.Section(
                    has_this ? offset - 1 : offset,
                    args - (struct_ret ? 1 : 0));

                // Set up locals. I'm making an assumption that there is a 
                // one to one and in order mapping of the locals with that
                // defined for the method body by Mono.
                Collection<VariableDefinition> variables = bb.RewrittenCalleeSignature.Resolve().Body.Variables;
                _locals = _stack.Section((int)_stack.Count, locals);
                for (int i = 0; i < locals; ++i)
                {
                    var tr = variables[i].VariableType;
                    TYPE type = new TYPE(tr);
                    VALUE value;
                    if (LLVM.GetTypeKind(type.IntermediateType) == TypeKind.PointerTypeKind)
                        value = new VALUE(LLVM.ConstPointerNull(type.IntermediateType));
                    else if (LLVM.GetTypeKind(type.IntermediateType) == TypeKind.DoubleTypeKind)
                        value = new VALUE(LLVM.ConstReal(LLVM.DoubleType(), 0));
                    else if (LLVM.GetTypeKind(type.IntermediateType) == TypeKind.IntegerTypeKind)
                        value = new VALUE(LLVM.ConstInt(type.IntermediateType, (ulong)0, true));
                    else if (LLVM.GetTypeKind(type.IntermediateType) == TypeKind.StructTypeKind)
                    {
                        var entry = bb.Entry.LlvmInfo.BasicBlock;
                        //var beginning = LLVM.GetFirstInstruction(entry);
                        //LLVM.PositionBuilderBefore(basic_block.Builder, beginning);
                        var new_obj = LLVM.BuildAlloca(bb.LlvmInfo.Builder, type.IntermediateType, "i" + INST.instruction_id++); // Allocates struct on stack, but returns a pointer to struct.
                        //LLVM.PositionBuilderAtEnd(bb.LlvmInfo.Builder, bb.BasicBlock);
                        value = new VALUE(new_obj);
                    }
                    else
                        throw new Exception("Unhandled type");
                    _stack.Push(value);
                }

                // Set up any thing else.
                for (int i = _stack.Size(); i < level; ++i)
                {
                    VALUE value = new VALUE(LLVM.ConstInt(LLVM.Int32Type(), (ulong)0, true));
                    _stack.Push(value);
                }
            }
            else if (bb._graph.Predecessors(bb).Count() == 1)
            {
                // We don't need phi functions--and can't with LLVM--
                // if there is only one predecessor. If it hasn't been
                // converted before this node, just create basic state.

                var pred = bb._graph.PredecessorEdges(bb).ToList()[0].From;
                var p_llvm_node = pred;
                var other = states_out[p_llvm_node];
                var size = other._stack.Count;
                for (int i = 0; i < size; ++i)
                {
                    var vx = other._stack[i];
                    _stack.Push(vx);
                }
                _struct_ret = _stack.Section(other._struct_ret.Base, other._struct_ret.Len);
                _this = _stack.Section(other._this.Base, other._this.Len);
                _arguments = _stack.Section(other._arguments.Base, other._arguments.Len);
                _locals = _stack.Section(other._locals.Base, other._locals.Len);
            }
            else // node._Predecessors.Count > 0
            {
                // As we cannot guarantee whether all predecessors are fulfilled,
                // make up something so we don't have problems.
                // Now, for every arg, local, stack, set up for merge.
                // Find a predecessor that has some definition.
                var pred = bb._graph.PredecessorEdges(bb).ToList()[0].From;
                for (int pred_ind = 0; pred_ind < bb._graph.Predecessors(bb).ToList().Count; ++pred_ind)
                {
                    var to_check = bb._graph.PredecessorEdges(bb).ToList()[pred_ind].From;
                    if (!visited.ContainsKey(to_check)) continue;
                    CFG.Vertex check_llvm_node = to_check;
                    if (!states_out.ContainsKey(check_llvm_node))
                        continue;
                    if (states_out[check_llvm_node] == null)
                        continue;
                    if (states_out[check_llvm_node]._stack == null)
                        continue;
                    pred = to_check;
                    break;
                }

                CFG.Vertex p_llvm_node = pred;
                int size = states_out[p_llvm_node]._stack.Count;
                for (int i = 0; i < size; ++i)
                {
                    {
                        VALUE f = new VALUE(LLVM.ConstInt(LLVM.Int32Type(), (ulong)0, true));
                        _stack.Push(f);
                    }
                    var count = bb._graph.Predecessors(bb).Count();
                    var value = states_out[p_llvm_node]._stack[i];
                    var v = value.V;
                    TypeRef tr = LLVM.TypeOf(v);
                    ValueRef res = LLVM.BuildPhi(bb.LlvmInfo.Builder, tr, "i" + INST.instruction_id++);
                    _phi.Add(res);
                    _stack[i] = new VALUE(res);
                }
                var other = states_out[p_llvm_node];
                _struct_ret = _stack.Section(other._struct_ret.Base, other._struct_ret.Len);
                _this = _stack.Section(other._this.Base, other._this.Len);
                _arguments = _stack.Section(other._arguments.Base, other._arguments.Len);
                _locals = _stack.Section(other._locals.Base, other._locals.Len);
            }
        }

        public STATE(STATE other)
        {
            _stack = new StackQueue<VALUE>();
            for (int i = 0; i < other._stack.Count; ++i)
            {
                _stack.Push(other._stack.PeekBottom(i));
            }
            _struct_ret = _stack.Section(other._struct_ret.Base, other._struct_ret.Len);
            _this = _stack.Section(other._this.Base, other._this.Len);
            _arguments = _stack.Section(other._arguments.Base, other._arguments.Len);
            _locals = _stack.Section(other._locals.Base, other._locals.Len);
        }

        public void OutputTrace(string indent)
        {
            int args = _arguments.Len;
            int locs = _locals.Len;
            System.Console.WriteLine(indent + "This size = " + _this.Len);
            System.Console.WriteLine(indent + "Args size = " + _arguments.Len);
            System.Console.WriteLine(indent + "Locals size = " + _locals.Len);
            System.Console.WriteLine(indent + "Stack size = " + _stack.Count);
            if (_this.Len > 0)
            {
                System.Console.WriteLine(indent + "[this (base " + _this.Base + ")");
                System.Console.WriteLine(indent + _this[0]);
                System.Console.WriteLine(indent + "]");
            }
            System.Console.WriteLine(indent + "[args (base " + _arguments.Base + ")");
            for (int i = 0; i < args; ++i)
            {
                System.Console.WriteLine(indent + _arguments[i]);
            }
            System.Console.WriteLine(indent + "]");
            System.Console.WriteLine(indent + "[locs (base " + _locals.Base + ")");
            for (int i = 0; i < locs; ++i)
            {
                System.Console.WriteLine(indent + _locals[i]);
            }
            System.Console.WriteLine(indent + "]");
            System.Console.WriteLine(indent + "[rest of stack (base " + (args + locs) + ")");
            // NB. Args includes "this" pointer.
            for (int i = args + locs; i < _stack.Size(); ++i)
            {
                System.Console.WriteLine(indent + _stack[i]);
            }
            System.Console.WriteLine(indent + "]");
            System.Console.WriteLine(indent + "[complete stack (base " + 0 + ")");
            for (int i = 0; i < _stack.Size(); ++i)
            {
                System.Console.WriteLine(indent + _stack[i]);
            }
            System.Console.WriteLine(indent + "]");
        }
    }
}
