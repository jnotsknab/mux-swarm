---
name: ai-prompt-optimizer
description: Optimize prompts for better LLM responses. Use when users want to improve prompt clarity, add structure, enhance specificity, or need prompt templates for specific tasks. Uses CodeAgent for analysis and generation, and MemoryAgent for storing optimized prompts.
---

# AI Prompt Optimizer

## Overview

This skill transforms vague or poorly structured prompts into optimized versions that produce better responses from LLMs. It analyzes prompts for clarity, structure, context, and constraints, then outputs enhanced versions with explanations.

## When to Use This Skill

- User requests: "improve this prompt", "make this clearer", "optimize my prompt"
- User has a vague prompt they want refined
- User needs a template for a specific task (coding, writing, research)
- User wants to understand why their prompt isn't working well
- Requests involving "prompt engineering", "better prompts", "write a better prompt"

## Agent Usage

### CodeAgent Tasks
- Analyze the original prompt for weaknesses
- Generate optimized prompt versions
- Create prompt templates for specific domains
- Explain optimization choices

### MemoryAgent Tasks
- Store successful optimized prompts for reuse
- Retrieve similar optimized prompts as references
- Maintain a library of prompt patterns

## Workflow

### Step 1: Analyze the Original Prompt
Use CodeAgent to evaluate the prompt against key criteria:
- Clarity: Is the intent unambiguous?
- Specificity: Are the requirements detailed enough?
- Structure: Is there a clear format for the response?
- Context: Does the prompt provide necessary background?
- Constraints: Are there explicit limits or requirements?

### Step 2: Identify Weaknesses
CodeAgent should categorize issues:
- Vague language ("make it good", "do it right")
- Missing output format specifications
- Insufficient context or background
- No examples of desired output
- Unclear persona or tone requirements

### Step 3: Generate Optimized Version
CodeAgent produces an improved prompt that:
- Adds clear, specific instructions
- Specifies output format (markdown, JSON, bullet points, etc.)
- Includes persona/tone guidance
- Provides examples when helpful
- Adds constraints and requirements

### Step 4: Explain Changes
CodeAgent provides a brief explanation of:
- What was changed and why
- How each change improves the output
- Suggestions for iterative refinement

## Example Usage

### Example 1: Vague Request

**User Request:** "Write about Python"

**Optimized Output:**
```
As a Python expert with 10 years of experience, write a comprehensive 
technical guide on Python for intermediate developers.

Requirements:
- Cover: data types, control flow, functions, OOP, and common libraries
- Include practical code examples for each topic
- Use clear headings and bullet points
- Target audience: developers who know another language
- Output format: Markdown with code blocks
- Length: 1500-2000 words
```

**Explanation:**
- Added persona ("Python expert")
- Specified target audience
- Listed specific topics to cover
- Required output format
- Added length constraints

---

### Example 2: Coding Task

**User Request:** "Help me with JavaScript"

**Optimized Output:**
```
You are a senior JavaScript engineer. Review the following function 
for bugs, performance issues, and best practices violations:

```javascript
function processData(items) {
  const results = [];
  for (let i = 0; i < items.length; i++) {
    if (items[i].active) {
      results.push(items[i].name);
    }
  }
  return results;
}
```

Provide:
1. Identified issues with explanation
2. Corrected code
3. Alternative ES6+ solutions
4. Performance comparison if relevant
```

**Explanation:**
- Defined persona and expertise level
- Provided specific code to review
- Listed exact output requirements (numbered list)
- Specified what feedback is needed

---

### Example 3: Research Request

**User Request:** "Find info about Docker"

**Optimized Output:**
```
Research Docker container orchestration in 2024. 

Context: I'm evaluating orchestration tools for a mid-sized company 
planning to migrate from monolithic to microservices architecture.

Requirements:
- Compare top 3 tools: Kubernetes, Docker Swarm, Apache Mesos
- Focus on: ease of setup, scaling capabilities, community support, cost
- Include pros and cons for each
- Provide a decision matrix
- Sources: official documentation and recent (2023-2024) industry reviews
- Output: structured markdown with tables
```

**Explanation:**
- Added specific technology context
- Defined comparison criteria
- Required output structure
- Specified recency of sources
- Added format requirements

## Resources

### references/agent_prompts.md
Contains prompt templates for common optimization scenarios:
- Technical documentation prompts
- Creative writing prompts
- Coding assistance prompts
- Research prompts
- Data analysis prompts

### Prompt Optimization Checklist

Use this checklist when optimizing:

- [ ] Clear objective statement
- [ ] Defined persona (if relevant)
- [ ] Target audience specified
- [ ] Output format specified
- [ ] Length constraints stated
- [ ] Required sections listed
- [ ] Examples provided
- [ ] Context/background included
- [ ] Constraints/limitations defined
- [ ] Tone/style guidance provided
