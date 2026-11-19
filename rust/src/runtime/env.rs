//! Environment frames and closures.

use std::rc::Rc;

use crate::binding::bound::Node;
use crate::values::JqValue;

/// The environment: the innermost frame, or `None` for the empty environment.
pub type EnvRef<'p> = Option<Rc<Env<'p>>>;

/// A filter argument: the argument's node with the caller's environment (§7.7).
pub struct Closure<'e, 'p> {
    pub node: &'p Node,
    pub env: &'e EnvRef<'p>,
}

/// What a frame holds: the variable or the variables of one binding, the closures of one call of a function
/// with parameters, or one label instance.
///
/// Rust: the closures of one call all close over the caller's environment, and their nodes are
/// the call's arguments, so the frame keeps that environment once beside the slice of arguments
/// instead of one closure object per argument (the reference's `Closure[]`).
pub enum Frame<'p> {
    /// The one variable of `E as $x`, of a `reduce` or of a `foreach`: no list to allocate.
    Value(JqValue),
    Values(Vec<JqValue>),
    Closures {
        arguments: &'p [Node],
        env: EnvRef<'p>,
    },
    Label(u64),
}

/// One frame of the environment, linked to the frame outside it. The resolver numbered the
/// frames, so a reference walks a known number of frames outward (bound.rs). A frame that is
/// shared is never changed (`rebind` changes one that nothing else holds). One evaluation runs on
/// one thread, so frames are counted with `Rc`.
pub struct Env<'p> {
    pub parent: EnvRef<'p>,
    pub frame: Frame<'p>,
}

impl<'p> Env<'p> {
    pub fn value(parent: &EnvRef<'p>, value: JqValue) -> EnvRef<'p> {
        Some(Rc::new(Env { parent: parent.clone(), frame: Frame::Value(value) }))
    }

    /// Binds `value` in a frame inside `parent`, in `frame`. A loop binds each of its values with
    /// it: when nothing kept the frame of the value before (a frame is kept by what closes over
    /// it, and that seldom outlives the body), the frame is used again instead of a new one.
    pub fn rebind(frame: &mut EnvRef<'p>, parent: &EnvRef<'p>, value: JqValue) {
        match frame.as_mut().and_then(Rc::get_mut) {
            Some(env) => env.frame = Frame::Value(value),
            None => *frame = Env::value(parent, value),
        }
    }

    pub fn values(parent: &EnvRef<'p>, values: Vec<JqValue>) -> EnvRef<'p> {
        Some(Rc::new(Env { parent: parent.clone(), frame: Frame::Values(values) }))
    }

    pub fn closures(parent: &EnvRef<'p>, arguments: &'p [Node], env: &EnvRef<'p>) -> EnvRef<'p> {
        Some(Rc::new(Env { parent: parent.clone(), frame: Frame::Closures { arguments, env: env.clone() } }))
    }

    pub fn label(parent: &EnvRef<'p>, label: u64) -> EnvRef<'p> {
        Some(Rc::new(Env { parent: parent.clone(), frame: Frame::Label(label) }))
    }

    /// The environment `hops` frames out from `env`.
    pub fn up<'e>(env: &'e EnvRef<'p>, hops: usize) -> &'e EnvRef<'p> {
        let mut env = env;
        for _ in 0..hops {
            match env {
                Some(frame) => env = &frame.parent,
                None => break,
            }
        }
        env
    }

    /// The variable in `slot` of the frame `hops` frames out.
    pub fn variable<'e>(env: &'e EnvRef<'p>, hops: usize, slot: usize) -> &'e JqValue {
        match Env::up(env, hops).as_deref() {
            Some(Env { frame: Frame::Value(value), .. }) if slot == 0 => value,
            Some(Env { frame: Frame::Values(values), .. }) => &values[slot],
            _ => panic!("The frame {hops} frames out holds no variables."),
        }
    }

    /// The closure in `slot` of the call frame `hops` frames out.
    pub fn closure<'e>(env: &'e EnvRef<'p>, hops: usize, slot: usize) -> Closure<'e, 'p> {
        match Env::up(env, hops).as_deref() {
            Some(Env { frame: Frame::Closures { arguments, env }, .. }) => Closure { node: &arguments[slot], env },
            _ => panic!("The frame {hops} frames out holds no closures."),
        }
    }

    /// The label instance of the frame `hops` frames out.
    pub fn label_at(env: &EnvRef<'p>, hops: usize) -> u64 {
        match Env::up(env, hops).as_deref() {
            Some(Env { frame: Frame::Label(label), .. }) => *label,
            _ => panic!("The frame {hops} frames out holds no label."),
        }
    }

    // The environments this frame keeps alive and is the last to use.
    fn release(&mut self, pending: &mut Vec<Rc<Env<'p>>>) -> EnvRef<'p> {
        if let Frame::Closures { env, .. } = &mut self.frame {
            if let Some(closed) = env.take() {
                if Rc::strong_count(&closed) == 1 {
                    pending.push(closed);
                }
            }
        }
        self.parent.take()
    }
}

// A chain of frames is as long as a pipe of bindings, and the closures of a deep recursion close
// over one another: frames are released by a loop, not by recursion.
impl Drop for Env<'_> {
    fn drop(&mut self) {
        let mut pending = Vec::new();
        let mut next = self.release(&mut pending);
        loop {
            while let Some(env) = next {
                next = match Rc::into_inner(env) {
                    Some(mut env) => env.release(&mut pending),
                    None => None,
                };
            }

            match pending.pop() {
                Some(env) => next = Some(env),
                None => break,
            }
        }
    }
}
